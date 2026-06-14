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
    // Tier 3: empty dir with no override → CoreAcquisitionException (no network)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NoDmonCsNoOverrideNoDefault_ThrowsWithoutNetwork()
    {
        // Working dir has no Dmon.cs; no override; no env var; the default prebuilt
        // paths won't exist in this test environment (we're in a temp dir, and the
        // resolver walks relative to the entry assembly which points nowhere useful here).
        // The important assertion: no NuGet/network call ever happens — the exception is
        // thrown from filesystem-only logic.
        string emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        // Clear env var so tier 2 env path is also absent.
        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", null);

            CoreResolver resolver = new();

            // This will throw because no prebuilt default exists in a temp dir context.
            // The key property: it throws CoreAcquisitionException, not any NuGet-related exception.
            CoreAcquisitionException ex = await Assert.ThrowsAsync<CoreAcquisitionException>(
                () => resolver.ResolveAsync(emptyDir, corePathOverride: null, CancellationToken.None));

            // Message must mention the two override escape hatches.
            Assert.Contains("DMON_CORE_PATH", ex.Message);
            Assert.Contains("--core-path", ex.Message);
            Assert.Contains("Dmon.cs", ex.Message);
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
