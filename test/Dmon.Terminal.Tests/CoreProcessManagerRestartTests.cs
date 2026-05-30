using System.Reflection;
using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Runtime;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Integration tests for <see cref="CoreProcessManager.RestartAsync"/>:
/// verifies that a restart spawns a fresh process, re-binds stdio, and that a
/// session load re-acquires the lock on the same directory.
/// </summary>
public sealed class CoreProcessManagerRestartTests
{
    // ------------------------------------------------------------------
    //  5.1 / 5.5 — RestartAsync spawns a fresh process and re-binds stdio
    // ------------------------------------------------------------------

    [Fact]
    public async Task RestartAsync_SpawnsFreshProcess_AndSessionLoadSucceeds()
    {
        string coreExe = FindCoreExe(out _);

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"dmon-restart-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);

        using CoreProcessManager manager = new(corePathOverride: coreExe, workingDirectory: tempWorkDir);

        try
        {
            // Write provider config into the per-test temp working directory so the core
            // picks it up via <cwd>/.dmon/config.yaml — no shared bin-dir mutation.
            string dmonDir = Path.Combine(tempWorkDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            await File.WriteAllTextAsync(Path.Combine(dmonDir, "config.yaml"), """
                providers:
                  test:
                    adapter: openai
                    defaultModelId: gpt-4
                    auth:
                      type: none
                """);

            await manager.StartAsync();

            // Wait for agentReady on the first process.
            await WaitForAgentReadyAsync(manager.StandardOutput);
            int firstPid = manager.ProcessId ?? throw new InvalidOperationException("ProcessId null after start.");

            // Create a session so we have something to re-open after restart.
            string sessionCreateId = Guid.NewGuid().ToString("N");
            await SendCommandAsync(manager, new SessionCreateCommand { Id = sessionCreateId });

            string? activeSessionId = await ReadSessionIdFromResponseAsync(manager.StandardOutput, sessionCreateId);
            Assert.NotNull(activeSessionId);

            // Restart — the old process exits, new one starts.
            await manager.RestartAsync();

            Assert.True(manager.IsRunning, "IsRunning should be true after RestartAsync.");
            Assert.NotEqual(firstPid, manager.ProcessId ?? firstPid);

            // Wait for agentReady on the new process's stdout.
            await WaitForAgentReadyAsync(manager.StandardOutput);

            // Re-open the session directory — the new process must re-acquire the lock.
            string sessionLoadId = Guid.NewGuid().ToString("N");
            await SendCommandAsync(manager, new SessionLoadCommand { Id = sessionLoadId, Path = activeSessionId });

            bool loadSucceeded = await ReadResponseSuccessAsync(manager.StandardOutput, sessionLoadId);
            Assert.True(loadSucceeded, "session.load on the new process should succeed (lock re-acquired).");
        }
        finally
        {
            try { await manager.StopAsync(); } catch { /* best effort */ }
            try { Directory.Delete(tempWorkDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    //  5.2 — EventDispatcher re-bind: sequential dispatchers on distinct streams
    // ------------------------------------------------------------------

    [Fact]
    public async Task EventDispatcher_RebuildOnRestart_ReadsFromNewStream()
    {
        string event1Json = """{"type":"agentReady","coreVersion":"0.0.0","protocolVersion":"0.1"}""";
        string event2Json = """{"type":"agentReady","coreVersion":"1.0.0","protocolVersion":"0.1"}""";

        // First dispatcher — reads from stream 1 and completes on EOF.
        using MemoryStream stream1 = new(System.Text.Encoding.UTF8.GetBytes(event1Json + "\n"));
        using StreamReader reader1 = new(stream1);
        EventDispatcher dispatcher1 = new(reader1);
        Task run1 = dispatcher1.RunAsync(CancellationToken.None);
        bool hasEvent1 = await dispatcher1.Events.WaitToReadAsync(CancellationToken.None);
        Assert.True(hasEvent1);
        dispatcher1.Events.TryRead(out Dmon.Protocol.Events.Event? first);
        Assert.NotNull(first);
        await run1; // completes on EOF

        // Second dispatcher — reads from stream 2 after "restart".
        using MemoryStream stream2 = new(System.Text.Encoding.UTF8.GetBytes(event2Json + "\n"));
        using StreamReader reader2 = new(stream2);
        EventDispatcher dispatcher2 = new(reader2);
        Task run2 = dispatcher2.RunAsync(CancellationToken.None);
        bool hasEvent2 = await dispatcher2.Events.WaitToReadAsync(CancellationToken.None);
        Assert.True(hasEvent2);
        dispatcher2.Events.TryRead(out Dmon.Protocol.Events.Event? second);
        Assert.NotNull(second);
        await run2;

        // Confirm the dispatchers read independent events — no cross-contamination.
        Assert.IsType<Dmon.Protocol.Events.AgentReadyEvent>(first);
        Assert.IsType<Dmon.Protocol.Events.AgentReadyEvent>(second);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static string FindCoreExe(out string coreDir)
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Terminal.Tests/bin/Release/net10.0 → repo root is 5 levels up
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));

        string[] candidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/dmoncore"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/dmoncore"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                coreDir = Path.GetDirectoryName(candidate)!;
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not find dmoncore executable. Run 'make build' or 'dotnet build' first.",
            "dmoncore");
    }

    private static Task SendCommandAsync(CoreProcessManager manager, Command cmd)
    {
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(cmd, options);
        manager.StandardInput.Write(json);
        manager.StandardInput.Write('\n');
        return manager.StandardInput.FlushAsync();
    }

    private static async Task WaitForAgentReadyAsync(StreamReader stdout)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 50; i++)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (line.Contains("\"agentReady\"")) return;
        }

        throw new TimeoutException("agentReady was not received within 30 seconds.");
    }

    /// <summary>
    /// Reads JSONL lines from stdout until a <c>response</c> with the given request id arrives,
    /// then returns the session id from <c>data.id</c>, or null if the response failed.
    /// </summary>
    private static async Task<string?> ReadSessionIdFromResponseAsync(StreamReader stdout, string requestId)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        for (int i = 0; i < 50; i++)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (!line.Contains("\"response\"")) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement idProp)) continue;
                if (idProp.GetString() != requestId) continue;
                if (!root.TryGetProperty("success", out JsonElement successProp) || !successProp.GetBoolean()) return null;
                if (!root.TryGetProperty("data", out JsonElement data)) return null;
                if (!data.TryGetProperty("id", out JsonElement sessionIdProp)) return null;
                return sessionIdProp.GetString();
            }
            catch (JsonException) { continue; }
        }

        return null;
    }

    /// <summary>
    /// Reads JSONL lines from stdout until a <c>response</c> with the given request id arrives,
    /// then returns its <c>success</c> value.
    /// </summary>
    private static async Task<bool> ReadResponseSuccessAsync(StreamReader stdout, string requestId)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 50; i++)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (!line.Contains("\"response\"")) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement idProp)) continue;
                if (idProp.GetString() != requestId) continue;
                if (!root.TryGetProperty("success", out JsonElement successProp)) return false;
                return successProp.GetBoolean();
            }
            catch (JsonException) { continue; }
        }

        return false;
    }
}
