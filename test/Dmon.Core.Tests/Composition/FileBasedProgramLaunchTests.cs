using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Dmon.Runtime;

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
    // OQ-B: stdout carries only clean JSONL frames up to agentReady — first build
    // ------------------------------------------------------------------

    [Fact]
    public async Task FileBasedProgram_FirstBuild_ReachesAgentReadyOverCleanJsonl()
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

            // Step 1: build via the production path (separate captured process — never touches the stdio channel).
            using CancellationTokenSource buildCts = new(TimeSpan.FromSeconds(120));
            await CoreProcessManager.BuildFileBasedProgramAsync(dmonCsPath, buildCts.Token);

            // Step 2: run --no-build and assert the core reaches an agentReady frame
            // over a stdout stream of clean JSONL frames only (never MSBuild/restore
            // output). A setupRequired frame legitimately precedes agentReady when no
            // provider is configured (e.g. CI); it is a valid JSONL frame.
            await AssertReachesAgentReadyOverCleanJsonlAsync(dmonCsPath, tempDir, timeoutSeconds: 15, context: "first build");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------
    // OQ-B: stdout carries only clean JSONL frames up to agentReady after a rebuild
    // ------------------------------------------------------------------

    [Fact]
    public async Task FileBasedProgram_Rebuild_AfterEdit_ReachesAgentReadyOverCleanJsonl()
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

            // Initial build via the production path.
            using CancellationTokenSource buildCts1 = new(TimeSpan.FromSeconds(120));
            await CoreProcessManager.BuildFileBasedProgramAsync(dmonCsPath, buildCts1.Token);

            // Simulate a source edit by appending a harmless trailing comment.
            // This forces the SDK to detect a change and recompile on the next build.
            await File.AppendAllTextAsync(dmonCsPath, "\n// reload-trigger\n");

            // Incremental rebuild via the production path (the staleness gate fires because of the edit).
            using CancellationTokenSource buildCts2 = new(TimeSpan.FromSeconds(120));
            await CoreProcessManager.BuildFileBasedProgramAsync(dmonCsPath, buildCts2.Token);

            // Run --no-build after the rebuild and assert clean JSONL up to agentReady.
            await AssertReachesAgentReadyOverCleanJsonlAsync(dmonCsPath, tempDir, timeoutSeconds: 15, context: "rebuild after edit");
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

    /// <summary>
    /// Runs <c>dotnet run &lt;Dmon.cs&gt; --no-build</c> and asserts the core reaches an
    /// <c>agentReady</c> frame over a stdout stream that carries <em>only</em> clean JSONL
    /// frames — proving the build phase never leaked MSBuild/restore output onto the
    /// stdio channel (ADR-019 OQ-B). <c>agentReady</c> is always emitted at startup, but a
    /// <c>setupRequired</c> frame precedes it when no provider is configured (e.g. CI), so
    /// reading only the first line is environment-dependent; every line up to and including
    /// <c>agentReady</c> must parse as a JSONL frame instead.
    /// </summary>
    private static async Task AssertReachesAgentReadyOverCleanJsonlAsync(
        string dmonCsPath,
        string workingDirectory,
        int timeoutSeconds,
        string context)
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

        List<string> framesRead = [];
        bool reachedAgentReady = false;
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
            // Bounded read: every line must be a clean JSONL frame (build/restore output
            // would not parse), and agentReady must appear among the startup frames.
            for (int i = 0; i < 20; i++)
            {
                string? line;
                try { line = await proc.StandardOutput.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { break; }

                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                framesRead.Add(line);
                if (AssertIsJsonlFrame(line, context) == "agentReady")
                {
                    reachedAgentReady = true;
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

        Assert.True(
            reachedAgentReady,
            $"[{context}] Core did not emit an agentReady JSONL frame within {timeoutSeconds}s. " +
            $"Frames read: {(framesRead.Count == 0 ? "(none)" : string.Join(" | ", framesRead))}");
    }

    /// <summary>
    /// Asserts <paramref name="line"/> is a clean JSONL protocol frame — parses as a JSON
    /// object with a non-empty <c>type</c> — proving no MSBuild/restore output leaked onto
    /// the stdout channel, and returns the <c>type</c> discriminator.
    /// </summary>
    private static string AssertIsJsonlFrame(string line, string context)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new Exception(
                $"[{context}] stdout line is not valid JSON — build output leaked into the JSONL channel.\n" +
                $"Line: {line}\n{ex.Message}", ex);
        }

        bool hasType = doc.RootElement.TryGetProperty("type", out JsonElement typeEl);
        Assert.True(hasType, $"[{context}] JSONL frame has no 'type' property. Line: {line}");
        string? type = typeEl.GetString();
        Assert.False(string.IsNullOrEmpty(type), $"[{context}] JSONL frame has an empty 'type'. Line: {line}");
        return type!;
    }
}
