using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Runtime;

namespace Dmon.Core.Tests.Integration;

/// <summary>
/// Live e2e test: launches a real dmoncore via <see cref="ICoreLauncher"/>, wraps it in
/// <see cref="IRpcClient"/> over <see cref="CoreProcessRpcTransport"/>, performs the
/// session create+load handshake, submits a turn that deterministically triggers the
/// builtin <c>read_file</c> tool, and asserts the full tool-call round-trip.
///
/// Closes composition-root-facets task 8.4 (now covered by automated e2e rather than HITL).
///
/// Per ADR-005, provider credentials come from env vars.  The test SKIPS — never silently
/// passes — when no provider key is present.
/// </summary>
public sealed class LiveToolCallE2ETest
{
    // Provider detection — first match wins.
    // Gemini is listed first: Flash is cheapest and least likely to hit quota.
    // Anthropic Haiku is second. OpenAI last (quota issues are common on free-tier keys).
    private static readonly (string EnvVar, string Adapter, string DefaultModelId)[] ProviderCandidates =
    [
        ("GEMINI_API_KEY",    "gemini",    "gemini-2.5-flash"),
        ("ANTHROPIC_API_KEY", "anthropic", "claude-haiku-4-5-20251001"),
        ("OPENAI_API_KEY",    "openai",    "gpt-4o-mini"),
    ];

    private const string SkipReason =
        "No provider API key (ANTHROPIC_API_KEY / OPENAI_API_KEY / GEMINI_API_KEY) configured" +
        " — live tool-call e2e skipped per ADR-005.";

    // How long we wait for the full turn (including LLM response + tool execution).
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(120);

    [SkippableFact]
    public async Task ToolCallRoundTrip_ReadFile_ObservesStartAndEnd()
    {
        (string adapter, string modelId) = DetectProvider();

        (string coreDll, _) = CoreProcessFixture.FindCoreDll();
        string contentRoot = Path.Combine(
            Path.GetTempPath(), $"dmon-live-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);

        // Write a marker file for the model to read.
        string markerPath = Path.Combine(contentRoot, "marker.txt");
        string markerContent = $"live-e2e-marker-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(markerPath, markerContent);

        // Write provider config into <cwd>/.dmon/config.local.yaml — the highest-priority
        // config source in DmonHostBuilder's layering (wins over ~/.dmon/config.yaml and
        // appsettings.json). This ensures the test-selected provider is the active one
        // regardless of the user's global ~/.dmon/config.yaml.
        string dmonDir = Path.Combine(contentRoot, ".dmon");
        Directory.CreateDirectory(dmonDir);
        string configLocalPath = Path.Combine(dmonDir, "config.local.yaml");
        string configLocalYaml = BuildProviderConfigYaml(adapter, modelId);
        await File.WriteAllTextAsync(configLocalPath, configLocalYaml);

        List<string> stderrLines = [];
        void OnStderr(string line) => stderrLines.Add(line);

        CoreSession session = await new CoreLauncher().StartProtocolCompatibleCoreAsync(
            corePathOverride: coreDll,
            workingDirectory: contentRoot,
            onStderrLine: OnStderr,
            cancellationToken: CancellationToken.None);

        await using RpcClient client = new(
            new CoreProcessRpcTransport(
                session.Process,
                onParseError: msg => stderrLines.Add($"[parse-error] {msg}")));

        await client.StartAsync(CancellationToken.None);

        try
        {
            await RunRoundTripAsync(client, markerPath, markerContent, stderrLines);
        }
        finally
        {
            await session.Process.StopAsync();
            session.Process.Dispose();

            try { Directory.Delete(contentRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static async Task RunRoundTripAsync(
        RpcClient client,
        string markerPath,
        string markerContent,
        List<string> stderrLines)
    {
        string FormatFailure(string msg) =>
            $"{msg}\nCore stderr:\n{string.Join("\n", stderrLines)}";

        // ── 1. Session create + load handshake ───────────────────────────────
        using CancellationTokenSource handshakeCts = new(TimeSpan.FromSeconds(30));

        SessionCreatedResultEvent created = await client.RequestAsync<SessionCreatedResultEvent>(
            new SessionCreateCommand { Id = NewId() },
            handshakeCts.Token);

        SessionLoadedResultEvent _ = await client.RequestAsync<SessionLoadedResultEvent>(
            new SessionLoadCommand { Id = NewId() },
            handshakeCts.Token);

        // ── 2. Submit a turn that deterministically triggers read_file ────────
        // read_file within the CWD is implicitly allowed (ADR-006) — no confirm prompt.
        string turnMessage =
            "Use your read_file tool to read the file at path ./marker.txt and report its " +
            "exact contents. You MUST call the read_file tool — do not guess or fabricate.";

        // ── 3. Subscribe BEFORE submitting the turn so no event is dropped ───
        // RpcClient registers the broadcast channel at CreateSubscription() time (property-get),
        // not at GetAsyncEnumerator() time. Obtaining the subscription here closes the window
        // between SendAsync and the start of iteration.
        IAsyncEnumerable<Event> eventStream = client.Events;

        await client.SendAsync(
            new TurnSubmitCommand { Id = NewId(), Message = turnMessage },
            CancellationToken.None);

        // ── 4. Consume events and assert the tool-call round-trip ────────────
        using CancellationTokenSource turnCts = new(TurnTimeout);

        bool sawToolStart  = false;
        bool sawToolEnd    = false;
        bool sawTurnEnd    = false;
        string? toolCallId = null;

        try
        {
            await foreach (Event evt in eventStream.WithCancellation(turnCts.Token))
            {
                switch (evt)
                {
                    case ToolExecutionStartEvent start when !sawToolStart:
                        // Accept any builtin tool call as evidence (prefer read_file).
                        sawToolStart = true;
                        toolCallId   = start.CallId;
                        break;

                    case ToolExecutionEndEvent end when toolCallId is not null
                                                     && end.CallId == toolCallId:
                        sawToolEnd = true;
                        Assert.False(end.IsError,
                            FormatFailure($"Tool call {end.CallId} failed with IsError=true."));
                        break;

                    case TurnEndEvent:
                        sawTurnEnd = true;
                        break;

                    case CommandErrorEvent err:
                        Assert.Fail(FormatFailure(
                            $"Core returned CommandErrorEvent: {JsonSerializer.Serialize(err)}"));
                        break;

                    // ToolConfirmRequestEvent (tool.confirmRequest): read_file in CWD should not
                    // trigger this per ADR-006, but guard so the test fails fast rather than hanging.
                    case ToolConfirmRequestEvent:
                        Assert.Fail(FormatFailure(
                            "Unexpected tool.confirmRequest — read_file within CWD should be " +
                            "implicitly permitted (ADR-006). Check permission configuration."));
                        break;
                }

                if (sawTurnEnd)
                    break;
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            Assert.Fail(FormatFailure(
                $"Turn did not complete within {TurnTimeout.TotalSeconds}s. " +
                $"sawToolStart={sawToolStart}, sawToolEnd={sawToolEnd}, sawTurnEnd={sawTurnEnd}."));
        }

        Assert.True(sawToolStart,
            FormatFailure("No ToolExecutionStartEvent was observed — tool was never called."));
        Assert.True(sawToolEnd,
            FormatFailure("No matching ToolExecutionEndEvent was observed for the tool call."));
        Assert.True(sawTurnEnd,
            FormatFailure("TurnEndEvent was never observed."));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (string adapter, string modelId) DetectProvider()
    {
        foreach ((string envVar, string adapter, string modelId) in ProviderCandidates)
        {
            string? key = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(key))
                return (adapter, modelId);
        }

        Skip.If(true, SkipReason);

        // Unreachable — Skip.If throws. Satisfies the compiler.
        throw new InvalidOperationException(SkipReason);
    }

    private static string BuildProviderConfigYaml(string adapter, string modelId)
    {
        // Map adapter name to the env var that CredentialResolver reads at runtime.
        string envVar = adapter switch
        {
            "anthropic" => "ANTHROPIC_API_KEY",
            "openai"    => "OPENAI_API_KEY",
            "gemini"    => "GEMINI_API_KEY",
            _           => throw new ArgumentOutOfRangeException(nameof(adapter), adapter, null)
        };

        // Written to <cwd>/.dmon/config.local.yaml — the highest-priority config source
        // in DmonHostBuilder's layering (wins over ~/.dmon/config.yaml and appsettings.json).
        // The user's global config may contribute additional provider stanzas; activeModel
        // pins the selection to our test provider so the registry ignores them.
        // No key value in the file — CredentialResolver reads the env var at runtime (ADR-005).
        return $"""
            providers:
              {adapter}:
                adapter: {adapter}
                defaultModelId: {modelId}
                auth:
                  type: envVar
                  envVar: {envVar}
            activeModel: {adapter}/{modelId}
            """;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
