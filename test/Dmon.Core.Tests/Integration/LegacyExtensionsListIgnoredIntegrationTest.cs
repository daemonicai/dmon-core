using System.Diagnostics;

namespace Dmon.Core.Tests.Integration;

/// <summary>
/// End-to-end integration test proving that a legacy <c>extensions:</c> list in
/// <c>config.yaml</c> is silently ignored. Under the composition-root-hosting model
/// (ADR-019) extensions are composed at compile time in <c>Dmon.cs</c>; the config
/// key is a no-op and must not prevent <c>agentReady</c> from being emitted.
/// </summary>
public sealed class LegacyExtensionsListIgnoredIntegrationTest
{
    [Fact]
    public async Task Daemon_StartsAndEmitsAgentReady_WhenConfigContainsLegacyExtensionsList()
    {
        (string coreDll, _) = CoreProcessFixture.FindCoreDll();

        string tempWorkDir = Path.Combine(Path.GetTempPath(), $"dmon-legacy-ext-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);

        Process? process = null;

        try
        {
            // Write an appsettings.json with a provider so SetupCheckService doesn't
            // emit setupRequired instead of agentReady. It must live in the core's
            // content root (its working directory) to avoid racing sibling fixtures.
            string appSettingsPath = Path.Combine(tempWorkDir, "appsettings.json");
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

            // Write a project config with a legacy extensions list that the core must ignore.
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
                $"agentReady was never received — legacy extensions list caused unexpected failure. " +
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
