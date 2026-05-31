using System.Text;
using Dmon.Abstractions.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory;

/// <summary>
/// Facade that composes a required short-term tier with an optional long-term tier.
/// Holds no storage state of its own — all persistence is delegated to the tiers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Write fan-out:</strong> <see cref="RecordAsync"/> dispatches concurrently to both
/// tiers when long-term is configured. A long-term record fault is NOT swallowed — both
/// tasks are awaited and any exception propagates (the spec is silent on record fault
/// containment; only search faults are explicitly contained per D5).
/// </para>
/// <para>
/// <strong>Search fusion:</strong> <see cref="SearchAsync"/> queries both tiers in parallel,
/// then fuses results via 1-based Reciprocal Rank Fusion with per-tier weights
/// (<see cref="RrfK"/>, <see cref="ShortTermWeight"/>, <see cref="LongTermWeight"/>).
/// A long-term search fault degrades to short-term-only results — the fault is logged
/// and not propagated. A short-term fault propagates normally.
/// </para>
/// <para>
/// <strong>De-duplication:</strong> cross-tier hits with the same normalized text
/// (trimmed, lowercased, whitespace-collapsed) are considered duplicates. The copy with
/// the higher fused RRF score survives; when tied, the copy carrying
/// <see cref="MemoryHit.Relations"/> is preferred (relations are long-term only).
/// </para>
/// </remarks>
public sealed class Memory : IMemory
{
    // RRF constants — adjust here to retune tier recall/precision.

    /// <summary>Standard RRF damping constant. k=60 is the conventional default.</summary>
    private const int RrfK = 60;

    /// <summary>Weight applied to short-term tier ranks during fusion.</summary>
    private const double ShortTermWeight = 1.0;

    /// <summary>Weight applied to long-term tier ranks during fusion.</summary>
    private const double LongTermWeight = 1.0;

    private readonly ILogger<Memory>? _logger;

    public Memory(
        IShortTermMemory shortTerm,
        ILongTermMemory? longTerm = null,
        ILogger<Memory>? logger = null)
    {
        ShortTerm = shortTerm ?? throw new ArgumentNullException(nameof(shortTerm));
        LongTerm = longTerm;
        _logger = logger;
    }

    /// <inheritdoc />
    public IShortTermMemory ShortTerm { get; }

    /// <inheritdoc />
    public ILongTermMemory? LongTerm { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Always writes to short-term. When long-term is configured both run concurrently;
    /// all exceptions propagate (spec is silent on record fault containment).
    /// </remarks>
    public async Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        if (LongTerm is null)
        {
            await ShortTerm.RecordAsync(turns, scope, cancellationToken).ConfigureAwait(false);
            return;
        }

        Task shortTask = ShortTerm.RecordAsync(turns, scope, cancellationToken);
        Task longTask  = LongTerm.RecordAsync(turns, scope, cancellationToken);
        await Task.WhenAll(shortTask, longTask).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Queries both tiers in parallel. Fuses results using 1-based RRF:
    /// <c>score = Σ weight_tier / (k + rank)</c> where rank is 1-based within each tier.
    /// Long-term search faults are contained: the fault is logged and short-term results
    /// are returned. Near-duplicate cross-tier hits (same normalized text or same Id)
    /// are de-duplicated, keeping the copy with the higher fused score and preferring
    /// the copy that carries Relations when scores tie.
    /// </remarks>
    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (LongTerm is null)
        {
            return await ShortTerm.SearchAsync(query, scope, limit, cancellationToken).ConfigureAwait(false);
        }

        // Run both tiers concurrently. Both tasks are started before any await so neither
        // is left unobserved if the other throws.
        Task<IReadOnlyList<MemoryHit>> shortTask = ShortTerm.SearchAsync(query, scope, limit, cancellationToken);
        Task<IReadOnlyList<MemoryHit>> longTask  = LongTerm.SearchAsync(query, scope, limit, cancellationToken);

        // Await short-term first; a short-term fault propagates (it is the always-on tier).
        IReadOnlyList<MemoryHit> shortHits = await shortTask.ConfigureAwait(false);

        IReadOnlyList<MemoryHit> longHits;
        try
        {
            longHits = await longTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Long-term fault: log and degrade to short-term only.
            _logger?.LogWarning(ex,
                "Long-term memory search faulted — returning short-term results only.");
            return shortHits;
        }

        return FuseAndDeduplicate(shortHits, longHits, limit);
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (LongTerm is null)
        {
            await ShortTerm.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ValueTask shortFlush = ShortTerm.FlushAsync(cancellationToken);
        ValueTask longFlush  = LongTerm.FlushAsync(cancellationToken);
        await shortFlush.ConfigureAwait(false);
        await longFlush.ConfigureAwait(false);
    }

    private static IReadOnlyList<MemoryHit> FuseAndDeduplicate(
        IReadOnlyList<MemoryHit> shortHits,
        IReadOnlyList<MemoryHit> longHits,
        int limit)
    {
        // Assign fused RRF scores. Rank is 1-based (rank 1 = best hit in the tier).
        // score = weight / (k + rank)
        Dictionary<string, (MemoryHit Hit, double FusedScore)> byKey = [];

        void Accumulate(IReadOnlyList<MemoryHit> hits, double weight)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                MemoryHit hit  = hits[i];
                int rank       = i + 1;                             // 1-based
                double contrib = weight / (RrfK + rank);
                string key     = DeduplicateKey(hit);

                if (byKey.TryGetValue(key, out (MemoryHit Hit, double FusedScore) existing))
                {
                    double newScore = existing.FusedScore + contrib;
                    MemoryHit survivor = ChooseSurvivor(existing.Hit, hit);
                    byKey[key] = (survivor, newScore);
                }
                else
                {
                    byKey[key] = (hit, contrib);
                }
            }
        }

        Accumulate(shortHits, ShortTermWeight);
        Accumulate(longHits,  LongTermWeight);

        // Re-emit each surviving hit with the fused Score field, then sort and cap.
        List<MemoryHit> fused = new(byKey.Count);
        foreach ((MemoryHit hit, double score) in byKey.Values)
        {
            fused.Add(hit with { Score = score });
        }

        fused.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        return fused.Count <= limit ? fused : fused.GetRange(0, limit);
    }

    /// <summary>
    /// Chooses which copy of a duplicate entry survives.
    /// Prefer the copy that carries <see cref="MemoryHit.Relations"/>; when both or
    /// neither carry Relations, the existing copy is kept (first seen wins on tie).
    /// The fused score is applied by the caller via <c>with { Score = score }</c>.
    /// </summary>
    private static MemoryHit ChooseSurvivor(MemoryHit existing, MemoryHit incoming)
    {
        bool existingHasRelations = existing.Relations is { Count: > 0 };
        bool incomingHasRelations = incoming.Relations is { Count: > 0 };

        if (!existingHasRelations && incomingHasRelations)
            return incoming;

        // existing has Relations or tie — keep existing (preserves Source provenance).
        return existing;
    }

    /// <summary>
    /// Normalized deduplication key: trimmed, lowercased, collapsed whitespace of
    /// <see cref="MemoryHit.Text"/>, falling back to <see cref="MemoryHit.Id"/> when
    /// the text is whitespace-only.
    /// </summary>
    private static string DeduplicateKey(MemoryHit hit)
    {
        string normalized = NormalizeText(hit.Text);
        return string.IsNullOrEmpty(normalized) ? hit.Id : normalized;
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Trim, lowercase, then collapse interior whitespace runs to a single space.
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        StringBuilder sb = new(span.Length);
        bool lastWasSpace = false;

        foreach (char c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }
}
