using System.Diagnostics;
using System.Text.Json;

namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Integration test for spec scenario "Newest compatible core is restored"
/// (core-runtime-acquisition/spec.md → "Version resolution by protocol compatibility").
///
/// Proves that a project pinning <c>dmoncore@0.1.*</c> (the SDK range that
/// <c>#:package dmoncore@0.1.*</c> compiles to) resolves the newest <c>0.1.x</c>
/// package (<c>0.1.9</c>) and never crosses the minor boundary to <c>0.2.0</c>,
/// even when all three versions are available in the feed.
///
/// Uses a stub <c>dmoncore</c> package (no library content) packed at three versions
/// into an isolated temp feed. A stub is correct here because the test exercises
/// NuGet's float-range resolution semantics — the <em>selection</em> mechanism that
/// both <c>#:package</c> and a standard <c>PackageReference</c> delegate to — not
/// dmoncore's runtime behaviour. Packing the real dmoncore at <c>0.1.x</c> would
/// require overriding its version to a Major.Minor that conflicts with
/// <c>ProtocolVersion.Current = "0.2"</c>, which is outside pack-core.sh's
/// supported path and irrelevant to what is being asserted.
/// </summary>
[Collection("ComposedCoreBuild")]
public sealed class VersionRangeRestoreTests : IAsyncLifetime
{
    private string? _tempFeed;
    private string? _tempDir;

    public async Task InitializeAsync()
    {
        _tempFeed = Path.Combine(Path.GetTempPath(), $"dmon-vr-feed-{Guid.NewGuid():N}");
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-vr-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempFeed);
        Directory.CreateDirectory(_tempDir);

        // Pack a minimal stub named "dmoncore" at three versions into the isolated temp feed.
        await PackStubAsync("0.1.3");
        await PackStubAsync("0.1.9");
        await PackStubAsync("0.2.0");
    }

    public Task DisposeAsync()
    {
        TryDelete(_tempFeed);
        TryDelete(_tempDir);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Scenario: Newest compatible core is restored
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProjectPinnedTo01Star_Restores019_NotVersion020()
    {
        string feed = _tempFeed!;
        string workDir = Path.Combine(_tempDir!, "restore-target");
        Directory.CreateDirectory(workDir);

        // A minimal project that pins dmoncore@0.1.*.
        // <PackageReference Version="0.1.*"/> is the resolved form of #:package dmoncore@0.1.*;
        // both delegate to the same NuGet float-range resolution.
        string csprojContent = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="dmoncore" Version="0.1.*" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(workDir, "pin-test.csproj"), csprojContent);

        // A nuget.config that points ONLY at the isolated temp feed — no nuget.org, no bleed.
        string nugetConfigContent = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-dmon-stub-feed" value="{feed}" />
              </packageSources>
            </configuration>
            """;
        File.WriteAllText(Path.Combine(workDir, "nuget.config"), nugetConfigContent);

        (int exitCode, string stdout, string stderr) = await RunDotnetAsync(
            "restore", $"\"{Path.Combine(workDir, "pin-test.csproj")}\"",
            workDir, timeoutSeconds: 120);

        Assert.True(
            exitCode == 0,
            $"dotnet restore failed (exit {exitCode}).\nstdout: {stdout}\nstderr: {stderr}");

        string assetsPath = Path.Combine(workDir, "obj", "project.assets.json");
        Assert.True(
            File.Exists(assetsPath),
            $"project.assets.json not found at {assetsPath} after restore.");

        string assetsJson = await File.ReadAllTextAsync(assetsPath);
        string resolvedVersion = ExtractDmoncoreVersion(assetsJson, assetsPath);

        Assert.Equal("0.1.9", resolvedVersion);
        Assert.NotEqual("0.2.0", resolvedVersion);
    }

    // ------------------------------------------------------------------
    // Implementation helpers
    // ------------------------------------------------------------------

    private async Task PackStubAsync(string version)
    {
        string stubDir = Path.Combine(_tempDir!, $"stub-{version}");
        Directory.CreateDirectory(stubDir);

        // A minimal project that produces a NuGet package named "dmoncore".
        // No source files — just the package identity. Version is set explicitly
        // so this test does not require git tags or MinVer.
        string csprojContent = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>dmoncore</PackageId>
                <Version>{version}</Version>
                <IsPackable>true</IsPackable>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(stubDir, "dmoncore-stub.csproj"), csprojContent);

        (int exitCode, string stdout, string stderr) = await RunDotnetAsync(
            "pack", $"dmoncore-stub.csproj -c Release -o \"{_tempFeed}\" --nologo",
            stubDir, timeoutSeconds: 60);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Stub pack at {version} failed (exit {exitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private static string ExtractDmoncoreVersion(string assetsJson, string assetsPath)
    {
        using JsonDocument doc = JsonDocument.Parse(assetsJson);
        JsonElement root = doc.RootElement;

        // project.assets.json structure: { "libraries": { "dmoncore/0.1.9": { ... }, ... } }
        if (!root.TryGetProperty("libraries", out JsonElement libraries))
        {
            throw new InvalidOperationException(
                $"project.assets.json at {assetsPath} has no 'libraries' key.");
        }

        foreach (JsonProperty lib in libraries.EnumerateObject())
        {
            // Key format is "PackageId/Version"
            if (lib.Name.StartsWith("dmoncore/", StringComparison.OrdinalIgnoreCase))
            {
                return lib.Name["dmoncore/".Length..];
            }
        }

        string libraryList = string.Join(", ", GetLibraryKeys(libraries));
        throw new InvalidOperationException(
            $"No 'dmoncore' entry found in project.assets.json. " +
            $"Libraries present: {libraryList}");
    }

    private static IEnumerable<string> GetLibraryKeys(JsonElement libraries)
    {
        foreach (JsonProperty lib in libraries.EnumerateObject())
            yield return lib.Name;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDotnetAsync(
        string verb, string arguments, string workingDirectory, int timeoutSeconds)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"{verb} {arguments}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
            throw new TimeoutException(
                $"dotnet {verb} timed out after {timeoutSeconds} seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return (proc.ExitCode, stdout, stderr);
    }

    private static void TryDelete(string? path)
    {
        if (path is not null)
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
        }
    }
}
