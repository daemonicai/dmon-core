using Dmon.Abstractions.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// Live integration smoke test against the real Meko MCP endpoint.
/// Tagged [Trait("Category","Live")] so CI and normal runs can exclude it:
///   dotnet test --filter "Category!=Live"
/// Gated on MEKO_API_KEY env var — returns early (no-op) when absent so the
/// normal dotnet test run stays green without any network traffic.
/// </summary>
[Trait("Category", "Live")]
public sealed class MekoLiveSmokeTests
{
    // Tools we depend on per D7 (live-verified 2026-05-29).
    private static readonly string[] RequiredTools =
    [
        "conversation_create",
        "memory_add",
        "memory_search",
        "memory_get_by_id",
        "memory_get_all",
        "memory_update",
        "memory_delete_by_id",
        "flush_pending_memory_candidates",
    ];

    /// <summary>
    /// 4.6 / 6.7 — Probes the real Meko endpoint end-to-end.
    ///
    /// <b>Hard assertions</b> (deterministic):
    ///   1. <c>conversation_create</c> returns a usable non-empty conversation id.
    ///   2. <c>AddFactAsync</c> with <see cref="MemoryScope.Agent"/> completes without throwing.
    ///   3. <c>AddFactAsync</c> with <see cref="MemoryScope.Session"/> and a hyphenated GUID
    ///      session id completes without throwing — exercises the <c>run_id</c> normalization
    ///      path (<c>MekoScopeMapping.ToRunId</c>).
    ///   4. Every required <c>memory_*</c> tool is present on the server.
    ///
    /// <b>Best-effort</b> (eventually-consistent — Meko does NOT guarantee read-your-writes):
    ///   - Polls <c>SearchAsync</c> and <c>ListAsync</c> for the stored marker up to ~30 s.
    ///   - Logs the outcome but does NOT fail the test if recall has not materialized.
    ///     Residual test memories may remain in Meko if cleanup cannot find the id.
    ///
    /// If MEKO_API_KEY is absent the method returns immediately — not a failure.
    /// </summary>
    [Fact]
    public async Task LiveSmoke_ToolProbe_RoundTrip_Cleanup()
    {
        string? apiKey = Environment.GetEnvironmentVariable("MEKO_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        string endpointStr = Environment.GetEnvironmentVariable("MEKO_ENDPOINT")
                             ?? "https://mcp.mekodata.ai/mcp";
        Uri endpoint = new(endpointStr);

        string smokeDatapack = Environment.GetEnvironmentVariable("MEKO_SMOKE_DATAPACK")
                               ?? string.Empty;
        // Use a plain hex GUID (no prefix) so run_id passes Meko's int(value,16) validation.
        string smokeSession = Guid.NewGuid().ToString("N");

        var options = new MekoLongTermOptions
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            DatapackId = smokeDatapack,
            SessionId = smokeSession,
            CaptureMode = MekoCaptureMode.EveryTurn,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken ct = cts.Token;

        // --- step 1: tool probe -----------------------------------------------
        await using McpClient probeClient = await BuildMcpClientAsync(options, ct);

        IList<McpClientTool> serverTools = await probeClient.ListToolsAsync(cancellationToken: ct);

        HashSet<string> serverToolNames = serverTools
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        string[] missing = RequiredTools.Where(r => !serverToolNames.Contains(r)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Required Meko tools not found on server.{Environment.NewLine}" +
            $"Missing: {string.Join(", ", missing)}{Environment.NewLine}" +
            $"Actual tools returned by server: {string.Join(", ", serverToolNames.Order())}");

        // --- step 2: build memory instance ------------------------------------
        string marker = Guid.NewGuid().ToString("N");
        string fact = $"dmon-smoke {marker}: the capital of Testland is Exampleburg";

        await using var invoker = new MekoToolInvoker(options, NullLoggerFactory.Instance);
        var context = new MekoMemoryContext(options);
        var memory = new MekoLongTermMemory(
            invoker,
            context,
            options,
            NullLogger<MekoLongTermMemory>.Instance);

        // --- step 2b: Session-scope HARD assertion with a hyphenated GUID session id -----
        // Exercises MekoScopeMapping.ToRunId normalization end-to-end.
        // Production dmon session ids are hyphenated GUIDs ("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx").
        // MekoScopeMapping.ToRunId normalises them to 32-char pure hex ("N" format) so that
        // Meko's server-side int(run_id, 16) validation does not reject the call.
        string hyphenatedSession = Guid.NewGuid().ToString(); // deliberate hyphenated form
        var sessionOptions = new MekoLongTermOptions
        {
            Endpoint = options.Endpoint,
            ApiKey = options.ApiKey,
            DatapackId = options.DatapackId,
            SessionId = hyphenatedSession,
            CaptureMode = MekoCaptureMode.EveryTurn,
        };
        await using var sessionInvoker = new MekoToolInvoker(sessionOptions, NullLoggerFactory.Instance);
        var sessionContext = new MekoMemoryContext(sessionOptions);
        var sessionMemory = new MekoLongTermMemory(
            sessionInvoker,
            sessionContext,
            sessionOptions,
            NullLogger<MekoLongTermMemory>.Instance);

        string sessionFact =
            $"dmon-smoke session {Guid.NewGuid():N}: run_id normalization check (hyphenated GUID session id)";

        // HARD assertion — must not throw. Failure here means the run_id normalization
        // path is broken and Meko rejected the int(run_id, 16) validation.
        await sessionMemory.AddFactAsync(sessionFact, MemoryScope.Session, ct);
        Console.WriteLine(
            $"[LiveSmoke] Session-scope AddFactAsync with hyphenated GUID session id succeeded " +
            $"(hyphenatedSession={hyphenatedSession}).");

        string? createdId = null;
        try
        {
            // --- step 3: AddFactAsync (HARD assertion — must not throw) -------
            // This exercises conversation_create then memory_add.
            await memory.AddFactAsync(fact, cancellationToken: ct);

            // --- step 4: flush ------------------------------------------------
            await memory.FlushAsync(ct);

            // --- step 5: best-effort recall poll (up to ~30 s) ----------------
            // Meko is eventually consistent — search recall is NOT guaranteed immediately.
            // We poll to give it a chance; but the test does NOT fail on 0 hits.
            IReadOnlyList<MemoryHit> searchHits = [];
            const int maxAttempts = 10;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                searchHits = await memory.SearchAsync(
                    $"capital Testland {marker}", cancellationToken: ct);

                if (searchHits.Count > 0)
                {
                    break;
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }

            if (searchHits.Count > 0)
            {
                // Recall succeeded — assert shape of returned hits.
                MemoryHit hit = searchHits[0];
                Assert.Equal(MemorySource.LongTerm, hit.Source);
                Assert.False(string.IsNullOrWhiteSpace(hit.Text),
                    "MemoryHit.Text must be populated for a successfully retrieved fact.");
                Assert.True(hit.Score >= 0,
                    $"MemoryHit.Score should be non-negative but was {hit.Score}.");

                createdId = hit.Id;
                Console.WriteLine(
                    $"[LiveSmoke] SearchAsync recalled the fact after polling. " +
                    $"id={createdId}, score={hit.Score:F4}");
            }
            else
            {
                // Best-effort: recall hasn't materialized within ~30 s.
                // This is acceptable for an eventually-consistent store.
                // Residual test memory may remain in Meko — acceptable per task 6.7.
                Console.WriteLine(
                    $"[LiveSmoke] SearchAsync did not recall the fact within ~30 s " +
                    $"(marker={marker}). This is acceptable; Meko is eventually consistent. " +
                    $"Try ListAsync for broader recall.");

                // Attempt ListAsync as a wider fallback to find the id for cleanup.
                IReadOnlyList<MemoryHit> listHits = await memory.ListAsync(cancellationToken: ct);
                MemoryHit? matched = listHits.FirstOrDefault(h =>
                    h.Text?.Contains(marker, StringComparison.Ordinal) == true);
                if (matched is not null)
                {
                    createdId = matched.Id;
                    Console.WriteLine($"[LiveSmoke] ListAsync found the fact for cleanup. id={createdId}");
                }
                else
                {
                    Console.WriteLine(
                        $"[LiveSmoke] ListAsync also did not find the marker. " +
                        $"Residual test memory may remain in Meko.");
                }
            }
        }
        finally
        {
            // --- step 6: cleanup (best-effort) --------------------------------
            // Residual test memories may remain if the id is not discoverable yet
            // (indexing lag). This is expected and documented.
            if (createdId is not null)
            {
#pragma warning disable CA1031
                try
                {
                    await memory.DeleteAsync(createdId, CancellationToken.None);
                    Console.WriteLine($"[LiveSmoke] Cleanup: deleted memory id={createdId}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LiveSmoke] Cleanup failed (best-effort; swallowed): {ex.Message}");
                }
#pragma warning restore CA1031
            }
        }
    }

    private static async Task<McpClient> BuildMcpClientAsync(
        MekoLongTermOptions options,
        CancellationToken cancellationToken)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = options.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {options.ApiKey}",
            },
        };

        var transport = new HttpClientTransport(transportOptions);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                              .ConfigureAwait(false);
    }
}
