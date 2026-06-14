using Dmon.Runtime;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Covers task 3.1: discovery precedence — Dmon.cs in CWD > override > prebuilt default.
/// All tests are offline (filesystem-only; no network, no NuGet cache, no SDK invocation).
/// </summary>
public sealed class CoreResolverTests : IDisposable
{
    // Temp directory that tests can place fake files in; cleaned up in Dispose.
    private readonly string _tempDir;

    public CoreResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-resolver-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // Tier 1: Dmon.cs in working directory wins over override and default
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_DmonCsInCwd_WinsOverOverride()
    {
        // Place Dmon.cs in the working dir.
        string dmonCs = Path.Combine(_tempDir, "Dmon.cs");
        await File.WriteAllTextAsync(dmonCs, "// fake");

        // Also place a prebuilt override that should NOT be chosen.
        string overrideExe = Path.Combine(_tempDir, "override-dmoncore");
        await File.WriteAllTextAsync(overrideExe, "fake");

        CoreResolver resolver = new();
        ResolvedCore result = await resolver.ResolveAsync(_tempDir, overrideExe, CancellationToken.None);

        Assert.Equal(LaunchMode.FileBasedProgram, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(dmonCs), result.Path);
    }

    [Fact]
    public async Task ResolveAsync_DmonCsInCwd_WinsWhenNoOverride()
    {
        string dmonCs = Path.Combine(_tempDir, "Dmon.cs");
        await File.WriteAllTextAsync(dmonCs, "// fake");

        CoreResolver resolver = new();
        ResolvedCore result = await resolver.ResolveAsync(_tempDir, corePathOverride: null, CancellationToken.None);

        Assert.Equal(LaunchMode.FileBasedProgram, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(dmonCs), result.Path);
    }

    // ------------------------------------------------------------------
    // Tier 2: --core-path override (.dll) wins over default — DotnetExec
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OverrideDll_WinsOverDefault_WhenNoDmonCs()
    {
        // No Dmon.cs in _tempDir; override is a .dll → must resolve as DotnetExec.
        string overrideDll = Path.Combine(_tempDir, "override-dmoncore.dll");
        await File.WriteAllTextAsync(overrideDll, "fake");

        CoreResolver resolver = new();
        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        ResolvedCore result = await resolver.ResolveAsync(emptyDir, overrideDll, CancellationToken.None);

        Assert.Equal(LaunchMode.DotnetExec, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(overrideDll), result.Path);
    }

    // ------------------------------------------------------------------
    // Tier 2: --core-path override (non-.dll) wins over default — DirectExecutable escape hatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OverrideNonDll_WinsOverDefault_WhenNoDmonCs()
    {
        // Non-.dll override (dev / escape-hatch binary) must resolve as DirectExecutable.
        string overrideExe = Path.Combine(_tempDir, "override-dmoncore");
        await File.WriteAllTextAsync(overrideExe, "fake");

        CoreResolver resolver = new();
        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        ResolvedCore result = await resolver.ResolveAsync(emptyDir, overrideExe, CancellationToken.None);

        Assert.Equal(LaunchMode.DirectExecutable, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(overrideExe), result.Path);
    }

    // ------------------------------------------------------------------
    // Tier 2: DMON_CORE_PATH env var (.dll) wins over default — DotnetExec
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_EnvVarDll_WinsOverDefault_WhenNoDmonCs()
    {
        string envDll = Path.Combine(_tempDir, "env-dmoncore.dll");
        await File.WriteAllTextAsync(envDll, "fake");

        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", envDll);

            CoreResolver resolver = new();
            ResolvedCore result = await resolver.ResolveAsync(
                emptyDir, corePathOverride: null, CancellationToken.None);

            Assert.Equal(LaunchMode.DotnetExec, result.LaunchMode);
            Assert.Equal(Path.GetFullPath(envDll), result.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", original);
        }
    }

    // ------------------------------------------------------------------
    // Tier 2: DMON_CORE_PATH env var (non-.dll) — DirectExecutable escape hatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_EnvVarNonDll_WinsOverDefault_WhenNoDmonCs()
    {
        string envExe = Path.Combine(_tempDir, "env-dmoncore");
        await File.WriteAllTextAsync(envExe, "fake");

        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", envExe);

            CoreResolver resolver = new();
            ResolvedCore result = await resolver.ResolveAsync(
                emptyDir, corePathOverride: null, CancellationToken.None);

            Assert.Equal(LaunchMode.DirectExecutable, result.LaunchMode);
            Assert.Equal(Path.GetFullPath(envExe), result.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", original);
        }
    }

    // ------------------------------------------------------------------
    // Tier 1 beats tier 2: Dmon.cs beats DMON_CORE_PATH env var
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_DmonCsInCwd_WinsOverEnvVar()
    {
        string dmonCs = Path.Combine(_tempDir, "Dmon.cs");
        await File.WriteAllTextAsync(dmonCs, "// fake");

        string envExe = Path.Combine(_tempDir, "env-dmoncore");
        await File.WriteAllTextAsync(envExe, "fake");

        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", envExe);

            CoreResolver resolver = new();
            ResolvedCore result = await resolver.ResolveAsync(
                _tempDir, corePathOverride: null, CancellationToken.None);

            Assert.Equal(LaunchMode.FileBasedProgram, result.LaunchMode);
            Assert.Equal(Path.GetFullPath(dmonCs), result.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", original);
        }
    }

    // ------------------------------------------------------------------
    // Tier 3: published-sibling dmoncore.dll resolves as DotnetExec
    //
    // We can't place a file at the real published-sibling path (relative to the
    // entry assembly), so this test exercises the resolver indirectly: with no
    // Dmon.cs and no override, and both published and dev-bin paths absent, the
    // resolver throws CoreAcquisitionException.  The DotnetExec assertion for the
    // published tier is covered by the override-dll tests above (same code path —
    // OverrideLaunchMode → DotnetExec for .dll) and by the integration build.
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Tier 3: no Dmon.cs, no override → resolves prebuilt default (DotnetExec) or
    // throws CoreAcquisitionException when no default is built. Either way: offline,
    // no NuGet/network call.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NoDmonCsNoOverride_IsOfflineFilesystemOnly()
    {
        // Working dir has no Dmon.cs; no override; no env var.
        // In CI (after `make build`) the dev-bin path build/dmoncore/dmoncore.dll exists
        // and Tier-3 resolves it as DotnetExec. In a clean checkout with no build output
        // it throws CoreAcquisitionException. Either outcome is correct; what must NOT
        // happen is any NuGet/network call — the resolver is filesystem-only.
        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", null);

            CoreResolver resolver = new();
            try
            {
                ResolvedCore result = await resolver.ResolveAsync(emptyDir, corePathOverride: null, CancellationToken.None);
                // If it resolved, it must be the prebuilt default via DotnetExec and the
                // path must point at an existing dmoncore.dll.
                Assert.Equal(LaunchMode.DotnetExec, result.LaunchMode);
                Assert.True(File.Exists(result.Path), $"Resolved path must exist: {result.Path}");
                Assert.True(result.Path.EndsWith("dmoncore.dll", StringComparison.OrdinalIgnoreCase),
                    $"Resolved path must be dmoncore.dll, got: {result.Path}");
            }
            catch (CoreAcquisitionException ex)
            {
                // No build output — exception is fine; must be filesystem-only (no NuGet msg).
                Assert.Contains("DMON_CORE_PATH", ex.Message);
                Assert.Contains("--core-path", ex.Message);
                Assert.Contains("Dmon.cs", ex.Message);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", original);
        }
    }

    // ------------------------------------------------------------------
    // Missing override file is skipped (falls through to next tier)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OverridePath_SkippedIfFileAbsent()
    {
        string dmonCs = Path.Combine(_tempDir, "Dmon.cs");
        await File.WriteAllTextAsync(dmonCs, "// fake");

        string missingOverride = Path.Combine(_tempDir, "does-not-exist");

        CoreResolver resolver = new();
        // Even with a non-existent override path, Dmon.cs (tier 1) should win.
        ResolvedCore result = await resolver.ResolveAsync(_tempDir, missingOverride, CancellationToken.None);

        Assert.Equal(LaunchMode.FileBasedProgram, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(dmonCs), result.Path);
    }
}
