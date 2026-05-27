using System.Diagnostics;

namespace Dmon.Core.Tests.Integration;

/// <summary>
/// End-to-end integration test proving the no-abort startup guarantee (task 3.4):
/// the daemon reaches <c>agentReady</c> even when a config-declared extension entry
/// fails to load. A nonexistent assembly path is used so the failure is immediate
/// and requires no network access.
/// </summary>
public sealed class BadConfigEntryIntegrationTest
{
    [Fact]
    public async Task Daemon_StartsAndEmitsAgentReady_WhenConfigEntryFails()
    {
        (string coreDll, string coreDir) = CoreProcessFixture.FindCoreDll();

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"dmon-bad-ext-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);

        Process? process = null;

        try
        {
            // Write an appsettings.json with a provider so SetupCheckService doesn't
            // emit setupRequired instead of agentReady.
            string appSettingsPath = Path.Combine(coreDir, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath, """
            {
              "providers": {
                "test": {
                  "adapter": "openai",
                  "defaultModelId": "gpt-4",
                  "auth": { "type": "none" }
                }
              }
            }
            """);

            // Write a project config with one bad extension entry.
            // The path does not exist, so the loader fails fast with no network I/O.
            string dmonDir = Path.Combine(tempWorkDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            await File.WriteAllTextAsync(Path.Combine(dmonDir, "config.yaml"), """
            extensions:
              - source: "./does-not-exist-ext.dll"
            """);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"exec \"{coreDll}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempWorkDir
            };

            process = new Process { StartInfo = psi };
            List<string> stderr = [];
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.Add(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            StreamReader stdout = process.StandardOutput;
            bool agentReadyReceived = false;

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

            for (int i = 0; i < 30; i++)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                    break;

                if (line.Contains("\"agentReady\""))
                {
                    agentReadyReceived = true;
                    break;
                }
            }

            string stderrText = string.Join("\n", stderr);
            string processState = process.HasExited
                ? $"Core exited with code {process.ExitCode}"
                : "Core still running";

            Assert.True(
                agentReadyReceived,
                $"agentReady was never received — bad config entry caused startup abort. " +
                $"{processState}. Core stderr:\n{stderrText}");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.StandardInput.Close();
                    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cts.Token);
                }
                catch
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { /* best effort */ }
                }
            }

            process?.Dispose();

            try { Directory.Delete(tempWorkDir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
