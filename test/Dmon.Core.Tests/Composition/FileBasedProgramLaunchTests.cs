using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Integration tests for the two-step file-based program launch path (ADR-019, task 3.4).
/// Asserts that <c>dotnet run Dmon.cs --no-build</c> stdout is pure JSONL — the first line
/// is always an <c>agentReady</c> frame, never MSBuild/restore output — both on first build
/// and on a rebuild-triggering reload.
/// Uses <see cref="ComposedCoreFeedFixture"/> to provision a local NuGet feed so the
/// test builds against a real packed dmoncore.
/// </summary>
[Collection("ComposedCoreBuild")]
public sealed class FileBasedProgramLaunchTests(ComposedCoreFeedFixture feed)
{
    // ------------------------------------------------------------------
    // OQ-B: first stdout line is agentReady, not build output — first build
    // ------------------------------------------------------------------

    [Fact]
    public async Task FileBasedProgram_FirstBuild_FirstStdoutLineIsAgentReady()
    {
        string repoRoot = LocateRepoRoot();
        string sourceCs = Path.Combine(repoRoot, "default-core", "Dmon.cs");

        string tempDir = Path.Combine(Path.GetTempPath(), $"dmon-fbp-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string dmonCsPath = Path.Combine(tempDir, "Dmon.cs");
            File.Copy(sourceCs, dmonCsPath);
            WriteNugetConfig(tempDir, feed.FeedPath);

            // Step 1: build (separate captured process — never touches the stdio channel).
            await RunDotnetBuildAsync(dmonCsPath, tempDir, timeoutSeconds: 120);

            // Step 2: run --no-build and capture the very first stdout line.
            string firstLine = await CaptureFirstStdoutLineAsync(dmonCsPath, tempDir, timeoutSeconds: 15);

            // The first line must be a valid JSON object with type == "agentReady".
            AssertIsAgentReadyJsonl(firstLine, "first build");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    // OQ-B: first stdout line is agentReady after a rebuild-triggering reload
    // ------------------------------------------------------------------

    [Fact]
    public async Task FileBasedProgram_Rebuild_AfterEdit_FirstStdoutLineIsAgentReady()
    {
        string repoRoot = LocateRepoRoot();
        string sourceCs = Path.Combine(repoRoot, "default-core", "Dmon.cs");

        string tempDir = Path.Combine(Path.GetTempPath(), $"dmon-fbp-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string dmonCsPath = Path.Combine(tempDir, "Dmon.cs");
            File.Copy(sourceCs, dmonCsPath);
            WriteNugetConfig(tempDir, feed.FeedPath);

            // Initial build.
            await RunDotnetBuildAsync(dmonCsPath, tempDir, timeoutSeconds: 120);

            // Simulate a source edit by appending a harmless trailing comment.
            // This forces the SDK to detect a change and recompile on the next build.
            await File.AppendAllTextAsync(dmonCsPath, "\n// reload-trigger\n");

            // Incremental rebuild (the staleness gate fires because of the edit).
            await RunDotnetBuildAsync(dmonCsPath, tempDir, timeoutSeconds: 120);

            // Run --no-build after the rebuild and capture the first stdout line.
            string firstLine = await CaptureFirstStdoutLineAsync(dmonCsPath, tempDir, timeoutSeconds: 15);

            AssertIsAgentReadyJsonl(firstLine, "rebuild after edit");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string LocateRepoRoot()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        // test/Dmon.Core.Tests/bin/Release/net10.0 → repo root is 5 levels up
        return Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));
    }

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

    private static async Task RunDotnetBuildAsync(
        string dmonCsPath,
        string workingDirectory,
        int timeoutSeconds)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(dmonCsPath);

        using Process proc = new() { StartInfo = psi };
        proc.Start();

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
            throw new TimeoutException($"dotnet build timed out after {timeoutSeconds}s.");
        }

        string output = await stdoutTask;
        string errors = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet build failed (exit {proc.ExitCode}).\nstdout: {output}\nstderr: {errors}");
        }
    }

    private static async Task<string> CaptureFirstStdoutLineAsync(
        string dmonCsPath,
        string workingDirectory,
        int timeoutSeconds)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(dmonCsPath);
        psi.ArgumentList.Add("--no-build");

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        // Drain stderr in the background so the child never blocks on a full pipe.
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();

        string? firstLine = null;
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
            firstLine = await proc.StandardOutput.ReadLineAsync(cts.Token);
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

        return firstLine
            ?? throw new InvalidOperationException(
                "Core process closed stdout without emitting any line.");
    }

    private static void AssertIsAgentReadyJsonl(string line, string context)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(line),
            $"[{context}] First stdout line must not be blank.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new Exception(
                $"[{context}] First stdout line is not valid JSON — build output leaked into the JSONL channel.\n" +
                $"First line: {line}\n{ex.Message}", ex);
        }

        bool hasType = doc.RootElement.TryGetProperty("type", out JsonElement typeEl);
        Assert.True(hasType, $"[{context}] First JSONL frame has no 'type' property. Line: {line}");
        Assert.Equal("agentReady", typeEl.GetString());
    }
}
