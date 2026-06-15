using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Terminal;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Integration test (a) for the composition-root hosting model (ADR-019):
/// <c>dmon init</c> produces a <c>Dmon.cs</c> that builds into a runnable core
/// emitting <c>agentReady</c> on stdout.
///
/// <see cref="InitFeedFixture"/> packs dmoncore and its contract trio into a unique
/// temp directory before the tests run and deletes it in teardown.
/// </summary>
public sealed class InitCommandTests(InitFeedFixture feed) : IClassFixture<InitFeedFixture>
{
    [Fact]
    public async Task Init_ScaffoldedDmonCs_BuildsAndEmitsAgentReady()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dmon-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Scaffold Dmon.cs via InitCommand.
            using StringWriter initOut = new();
            using StringWriter initErr = new();
            int exitCode = InitCommand.Run(tempDir, initOut, initErr);
            Assert.Equal(0, exitCode);

            string dmonCsPath = Path.Combine(tempDir, "Dmon.cs");
            Assert.True(File.Exists(dmonCsPath), "InitCommand should create Dmon.cs.");

            // Write a nuget.config pointing to the fixture's temp feed so the scaffolded
            // Dmon.cs can restore dmoncore without network access to nuget.org.
            WriteNugetConfig(tempDir, feed.FeedPath);

            // Build the scaffolded file-based program.
            await RunDotnetAsync("build", dmonCsPath, tempDir, timeoutSeconds: 120);

            // Run the built core; it must emit agentReady on stdout.
            string? agentReadyLine = await RunAndReadAgentReadyAsync(dmonCsPath, tempDir);
            Assert.NotNull(agentReadyLine);

            using JsonDocument doc = JsonDocument.Parse(agentReadyLine);
            string? eventType = doc.RootElement.GetProperty("type").GetString();
            Assert.Equal("agentReady", eventType);

            string? protocolVersion = doc.RootElement.GetProperty("protocolVersion").GetString();
            Assert.Equal(ProtocolVersion.Current, protocolVersion);
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public void Init_ExistingDmonCs_FailsWithNonZeroExit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dmon-init-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Dmon.cs"), "// existing");

            using StringWriter initOut = new();
            using StringWriter initErr = new();
            int exitCode = InitCommand.Run(tempDir, initOut, initErr);

            Assert.Equal(1, exitCode);
            Assert.Contains("already exists", initErr.ToString());
        }
        finally
        {
            SafeDelete(tempDir);
        }
    }

    [Fact]
    public void Init_Scaffold_ContainsProtocolPinAndDmonHostCall()
    {
        string scaffold = InitCommand.BuildScaffold();

        Assert.Contains($"#:package dmoncore@{ProtocolVersion.Current}.*", scaffold);
        Assert.Contains($"#:package Dmon.Tools.Builtin@{ProtocolVersion.Current}.*", scaffold);
        Assert.Contains("DmonHost.CreateBuilder", scaffold);
        Assert.Contains(".AddBuiltinTools()", scaffold);
        Assert.Contains(".Build()", scaffold);
        Assert.Contains(".RunAsync()", scaffold);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void WriteNugetConfig(string directory, string feedPath)
    {
        string content = $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local-dmon-feed" value="{feedPath}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""";
        File.WriteAllText(Path.Combine(directory, "nuget.config"), content);
    }

    private static async Task RunDotnetAsync(
        string verb,
        string targetPath,
        string workingDirectory,
        int timeoutSeconds)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"{verb} \"{targetPath}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        // Read stdout and stderr concurrently to avoid the pipe-buffer deadlock.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"dotnet {verb} timed out after {timeoutSeconds}s.");
        }

        string output = await stdoutTask;
        string errors = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {verb} failed (exit {proc.ExitCode}).\nstdout: {output}\nstderr: {errors}");
        }
    }

    private static async Task<string?> RunAndReadAgentReadyAsync(string dmonCsPath, string workingDirectory)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"run --no-build \"{dmonCsPath}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        string? agentReadyLine = null;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        try
        {
            for (int i = 0; i < 50; i++)
            {
                string? line;
                try { line = await proc.StandardOutput.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { break; }

                if (line is null) break;
                if (line.Contains("\"agentReady\""))
                {
                    agentReadyLine = line;
                    break;
                }
            }
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { /* best effort */ }
            try
            {
                using CancellationTokenSource exitCts = new(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(exitCts.Token);
            }
            catch { /* best effort */ }

            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }

        return agentReadyLine;
    }

    private static void SafeDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
