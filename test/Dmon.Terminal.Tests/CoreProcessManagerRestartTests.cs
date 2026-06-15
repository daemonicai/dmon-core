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
        ResolvedCore resolvedCore = FindCoreDll();

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"dmon-restart-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);

        using CoreProcessManager manager = new(resolvedCore, workingDirectory: tempWorkDir);

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
    //  5.2 — RpcClient re-bind: sequential clients on distinct streams read
    //         independent events with no cross-contamination.
    // ------------------------------------------------------------------

    [Fact]
    public async Task RpcClient_RebuildOnRestart_ReadsFromNewStream()
    {
        string event1Json = """{"type":"agentReady","coreVersion":"0.0.0","protocolVersion":"0.1"}""";
        string event2Json = """{"type":"agentReady","coreVersion":"1.0.0","protocolVersion":"0.1"}""";

        // First client — reads from stream 1 and completes on EOF.
        using MemoryStream stream1 = new(System.Text.Encoding.UTF8.GetBytes(event1Json + "\n"));
        using StreamReader reader1 = new(stream1);
        using StringWriter writer1 = new();
        await using RpcClient client1 = new(new CoreProcessRpcTransport(reader1, writer1));
        IAsyncEnumerable<Dmon.Protocol.Events.Event> stream1Events = client1.Events;
        await client1.StartAsync(CancellationToken.None);

        Dmon.Protocol.Events.Event? first = null;
        await foreach (Dmon.Protocol.Events.Event evt in stream1Events)
        {
            first = evt;
            break; // take the first event and stop; stream completes on EOF
        }

        // Second client — reads from stream 2 after "restart".
        using MemoryStream stream2 = new(System.Text.Encoding.UTF8.GetBytes(event2Json + "\n"));
        using StreamReader reader2 = new(stream2);
        using StringWriter writer2 = new();
        await using RpcClient client2 = new(new CoreProcessRpcTransport(reader2, writer2));
        IAsyncEnumerable<Dmon.Protocol.Events.Event> stream2Events = client2.Events;
        await client2.StartAsync(CancellationToken.None);

        Dmon.Protocol.Events.Event? second = null;
        await foreach (Dmon.Protocol.Events.Event evt in stream2Events)
        {
            second = evt;
            break;
        }

        // Confirm the clients read independent events — no cross-contamination.
        Assert.NotNull(first);
        Assert.NotNull(second);
        Dmon.Protocol.Events.AgentReadyEvent ready1 = Assert.IsType<Dmon.Protocol.Events.AgentReadyEvent>(first);
        Dmon.Protocol.Events.AgentReadyEvent ready2 = Assert.IsType<Dmon.Protocol.Events.AgentReadyEvent>(second);
        Assert.Equal("0.0.0", ready1.CoreVersion);
        Assert.Equal("1.0.0", ready2.CoreVersion);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static ResolvedCore FindCoreDll()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Terminal.Tests/bin/<cfg>/net10.0/ → repo root is 5 levels up
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));

        // Prebuilt default-core closure produced by `make build-core`
        // (publish of default-core/Dmon.cs into build/dmoncore/).
        string dll = Path.Combine(repoRoot, "build/dmoncore/dmoncore.dll");
        if (File.Exists(dll))
            return new ResolvedCore(Path.GetFullPath(dll), LaunchMode.DotnetExec);

        throw new FileNotFoundException(
            "Could not find build/dmoncore/dmoncore.dll. Run 'make build-core' first.",
            "dmoncore.dll");
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
    /// Reads JSONL lines from stdout until a <c>session.createResult</c> with the given
    /// command id arrives, then returns the session id from <c>session.id</c>, or null on failure.
    /// </summary>
    private static async Task<string?> ReadSessionIdFromResponseAsync(StreamReader stdout, string requestId)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        for (int i = 0; i < 50; i++)
        {
            string? line;
            try { line = await stdout.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;
            if (!line.Contains("\"session.createResult\"")) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement idProp)) continue;
                if (idProp.GetString() != requestId) continue;
                if (!root.TryGetProperty("session", out JsonElement session)) return null;
                if (!session.TryGetProperty("id", out JsonElement sessionIdProp)) return null;
                return sessionIdProp.GetString();
            }
            catch (JsonException) { continue; }
        }

        return null;
    }

    /// <summary>
    /// Reads JSONL lines from stdout until a <c>session.loadResult</c> or <c>commandError</c>
    /// with the given command id arrives, then returns <c>true</c> for load success.
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

            bool isLoadResult = line.Contains("\"session.loadResult\"");
            bool isError      = line.Contains("\"commandError\"");
            if (!isLoadResult && !isError) continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("id", out JsonElement idProp)) continue;
                if (idProp.GetString() != requestId) continue;
                // session.loadResult = success; commandError = failure.
                return isLoadResult;
            }
            catch (JsonException) { continue; }
        }

        return false;
    }
}
