using System.Security.Cryptography;
using System.Text;
using Dmon.Memory.Embedding;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Tests.Stubs;

/// <summary>
/// Deterministic, L2-normalized 768-dim embedding stub — no GGUF, no network.
///
/// Resolution order for each input string (already-prefixed by the call site):
///   1. An explicit override registered via <see cref="SetVector"/> — use verbatim
///      (caller is responsible for normalization if they want unit vectors).
///   2. A group override registered via <see cref="SetGroupVector"/> — all strings
///      sharing the same group key get the same vector, producing cosine distance ≈ 0.
///   3. Hash-derived: SHA-256 the UTF-8 bytes of the input, fold into 768 floats,
///      L2-normalize. Same input always → same unit vector.
///
/// This lets tests engineer specific vector-similarity relationships:
///   - Two inputs that should be "vector-close" → assign them the same group key.
///   - Two inputs that should be "vector-distant" → leave them with hash-derived vectors
///     (SHA-256 of different strings produces uncorrelated unit vectors).
///
/// Throwing behaviour: if <see cref="ShouldThrowOn"/> contains an exact match for the
/// input, the call throws <see cref="InvalidOperationException"/>. This supports
/// transactional-consistency tests (fault injection).
/// </summary>
internal sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = NomicEmbedding.Dimensions;

    // input string → explicit override vector (not automatically normalized)
    private readonly Dictionary<string, float[]> _overrides = new(StringComparer.Ordinal);

    // group-key → shared unit vector; inputs registered under the same key get that vector
    private readonly Dictionary<string, float[]> _groups = new(StringComparer.Ordinal);

    // input → group key
    private readonly Dictionary<string, string> _inputToGroup = new(StringComparer.Ordinal);

    // inputs that cause a throw
    private readonly HashSet<string> _throwOn = new(StringComparer.Ordinal);

    public EmbeddingGeneratorMetadata Metadata { get; } = new("stub", null, "stub", Dimensions);

    // ── Configuration helpers ────────────────────────────────────────────────

    /// <summary>Sets an exact vector for <paramref name="input"/> (call-site prefixed).</summary>
    public void SetVector(string input, float[] vector) => _overrides[input] = vector;

    /// <summary>
    /// Registers all <paramref name="inputs"/> to share the same stable unit vector
    /// derived from <paramref name="groupKey"/>.  Inputs in the same group will have
    /// cosine distance ≈ 0 relative to each other and to any other input in the group.
    /// </summary>
    public void SetGroupVector(string groupKey, params string[] inputs)
    {
        if (!_groups.TryGetValue(groupKey, out float[]? vec))
        {
            vec = HashToUnitVector(groupKey);
            _groups[groupKey] = vec;
        }
        foreach (string input in inputs)
            _inputToGroup[input] = groupKey;
    }

    /// <summary>
    /// After calling this, <see cref="GenerateAsync"/> throws on any input equal to
    /// <paramref name="input"/>.  Used for fault-injection tests.
    /// </summary>
    public void ShouldThrowOn(string input) => _throwOn.Add(input);

    // ── IEmbeddingGenerator ──────────────────────────────────────────────────

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string[] inputs = values as string[] ?? values.ToArray();
        Embedding<float>[] results = new Embedding<float>[inputs.Length];

        for (int i = 0; i < inputs.Length; i++)
        {
            string input = inputs[i];

            if (_throwOn.Contains(input))
                throw new InvalidOperationException($"StubEmbeddingGenerator: forced fault for input '{input}'.");

            float[] vec;
            if (_overrides.TryGetValue(input, out float[]? overrideVec))
            {
                vec = (float[])overrideVec.Clone();
            }
            else if (_inputToGroup.TryGetValue(input, out string? groupKey))
            {
                vec = (float[])_groups[groupKey].Clone();
            }
            else
            {
                vec = HashToUnitVector(input);
            }

            results[i] = new Embedding<float>(vec);
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(EmbeddingGeneratorMetadata))
            return Metadata;
        if (serviceType.IsInstanceOfType(this))
            return this;
        return null;
    }

    public void Dispose() { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a deterministic, L2-normalized 768-dim float vector from <paramref name="seed"/>.
    /// Uses SHA-256 as the PRNG seed: hash the UTF-8 bytes repeatedly until we have enough
    /// floats, then L2-normalize.
    /// </summary>
    private static float[] HashToUnitVector(string seed)
    {
        float[] vector = new float[Dimensions];
        // Use the seed bytes directly as initial state: round 0 hashes (seed || 0),
        // round 1 hashes (H(seed||0) || 1), etc.  Deterministic, no pre-hash needed.
        byte[] hash = Encoding.UTF8.GetBytes(seed);

        // Fill the vector by chaining hashes.
        int filled = 0;
        int round = 0;
        while (filled < Dimensions)
        {
            // Feed round counter into the hash for each pass.
            byte[] input = new byte[hash.Length + 4];
            hash.CopyTo(input, 0);
            BitConverter.GetBytes(round).CopyTo(input, hash.Length);
            hash = SHA256.HashData(input);
            round++;

            for (int b = 0; b + 3 < hash.Length && filled < Dimensions; b += 4, filled++)
            {
                // Map [0,255]^4 → float in [-1,+1].
                int raw = BitConverter.ToInt32(hash, b);
                vector[filled] = raw / (float)int.MaxValue;
            }
        }

        return EmbeddingMath.L2NormalizeInPlace(vector);
    }
}
