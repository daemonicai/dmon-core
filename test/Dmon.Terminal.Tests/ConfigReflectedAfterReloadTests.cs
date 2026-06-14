using System.Collections.Concurrent;
using System.Reflection;
using Dmon.Runtime;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Integration test for task 5.6: an edited <c>config.yaml</c> is reflected in
/// the effective extension set after <c>RestartAsync</c>.
///
/// Strategy: write a project config referencing a nonexistent assembly A; start the
/// core; observe stderr (structured JSON log) for the warning that mentions "A".
/// Then edit the config to reference nonexistent assembly B; call RestartAsync;
/// observe a fresh stderr warning that mentions "B". This proves the fresh core
/// re-read the file on disk — no network access required.
///
/// The startup extension loader (StartupExtensionLoader) logs failures via ILogger,
/// which writes structured JSON to stderr. It does not emit extensionError events to
/// stdout during startup. Stderr is the authoritative observable for startup load failures.
/// </summary>
public sealed class ConfigReflectedAfterReloadTests
{
    [Fact]
    public async Task AfterReload_FreshCoreReflectsEditedConfig()
    {
        ResolvedCore resolvedCore = FindCoreDll();

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"dmon-config-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);

        // Collect stderr lines from all process lifetimes. The onStderrLine callback fires
        // from a background thread; ConcurrentQueue handles concurrent enqueue/iterate safely.
        ConcurrentQueue<string> allStderr = new();

        using CoreProcessManager manager = new(
            resolvedCore,
            workingDirectory: tempWorkDir,
            onStderrLine: line => allStderr.Enqueue(line));

        try
        {
            string dmonDir = Path.Combine(tempWorkDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            string configPath = Path.Combine(dmonDir, "config.yaml");

            // Config A: provider stanza (for agentReady) plus a nonexistent extension assembly.
            // The provider block is written here so the core does not emit setupRequired —
            // it is loaded via <cwd>/.dmon/config.yaml, not a shared appsettings.json.
            await File.WriteAllTextAsync(configPath, """
                providers:
                  test:
                    adapter: openai
                    defaultModelId: gpt-4
                    auth:
                      type: none
                extensions:
                  - source: "./does-not-exist-A.dll"
                """);

            await manager.StartAsync();

            // Wait for agentReady — at that point StartupExtensionLoader has already run and
            // logged any failures to stderr.
            await WaitForAgentReadyAsync(manager.StandardOutput);

            // Poll until "does-not-exist-A" appears in captured stderr (logging is async).
            await WaitForStderrSubstringAsync(
                allStderr, "does-not-exist-A",
                failMessage: "Expected stderr to contain 'does-not-exist-A'.");

            // Edit the config on disk before restarting — update extension ref only;
            // provider stanza is preserved so the restarted core still reaches agentReady.
            await File.WriteAllTextAsync(configPath, """
                providers:
                  test:
                    adapter: openai
                    defaultModelId: gpt-4
                    auth:
                      type: none
                extensions:
                  - source: "./does-not-exist-B.dll"
                """);

            // Drain collected lines so we can assert on only post-restart output.
            while (allStderr.TryDequeue(out _)) { }

            await manager.RestartAsync();

            // Wait for agentReady on the fresh process.
            await WaitForAgentReadyAsync(manager.StandardOutput);

            // Poll until "does-not-exist-B" appears in captured stderr.
            await WaitForStderrSubstringAsync(
                allStderr, "does-not-exist-B",
                failMessage: "Expected stderr to contain 'does-not-exist-B' after reload.");

            string stderrB = string.Join("\n", allStderr);
            Assert.False(
                stderrB.Contains("does-not-exist-A", StringComparison.OrdinalIgnoreCase),
                "Stderr after reload should not reference the old extension source 'does-not-exist-A'.");
        }
        finally
        {
            try { await manager.StopAsync(); } catch { /* best effort */ }
            try { Directory.Delete(tempWorkDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static ResolvedCore FindCoreDll()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));

        string[] candidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/dmoncore.dll"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/dmoncore.dll"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return new ResolvedCore(Path.GetFullPath(candidate), LaunchMode.DotnetExec);
        }

        throw new FileNotFoundException(
            "Could not find dmoncore.dll. Run 'make build' or 'dotnet build' first.",
            "dmoncore.dll");
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
    /// Polls <paramref name="buffer"/> every 50 ms until <paramref name="substring"/> appears
    /// in the joined content, or the 30-second deadline passes.
    /// <para>
    /// <see cref="ConcurrentQueue{T}"/> enumeration is snapshot-safe, so concurrent
    /// enqueues from the stderr callback do not require additional locking here.
    /// </para>
    /// </summary>
    private static async Task WaitForStderrSubstringAsync(
        ConcurrentQueue<string> buffer,
        string substring,
        string failMessage)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        while (!cts.Token.IsCancellationRequested)
        {
            if (string.Join("\n", buffer).Contains(substring, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // One final check after timeout expiry.
        string final = string.Join("\n", buffer);
        Assert.Fail($"{failMessage} Timed out after 30 s. Stderr captured:\n{final}");
    }
}
