using System.Runtime.InteropServices;

namespace Dmon.Providers.Mlx.Tests;

// ---------------------------------------------------------------------------
// 3.2 — IsApplicable (injected OS/arch/uv-resolve seams)
// ---------------------------------------------------------------------------

public sealed class IsApplicableTests
{
    [Fact]
    public void IsApplicable_NotMacOs_ReturnsFalse_AndFiresWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => false,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("not running macOS", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsButNotArm64_ReturnsFalse_AndFiresDistinctWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.X64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        Assert.Contains("arm64", warnings[0]);
        Assert.Contains("non-arm64", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_UvUnresolved_ReturnsFalse_AndFiresUvRemediation()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => null,
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.False(result);
        Assert.Single(warnings);
        // Remediation must name "uv" — the missing prerequisite.
        Assert.Contains("uv", warnings[0]);
    }

    [Fact]
    public void IsApplicable_MacOsArm64_UvResolvable_ReturnsTrue_WithNoWarning()
    {
        List<string> warnings = [];
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => "/usr/local/bin/uv",
            onWarning: w => warnings.Add(w));

        bool result = sut.IsApplicable();

        Assert.True(result);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IsApplicable_InvokesResolveUvPathOverride_NotSystemPath()
    {
        // The override must be consulted — this proves no real PATH I/O occurs.
        bool overrideCalled = false;
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();
        using MlxProviderExtension sut = new(
            opts,
            isMacOsOverride: () => true,
            osArchitectureOverride: () => Architecture.Arm64,
            resolveUvPathOverride: () => { overrideCalled = true; return "/injected/uv"; },
            onWarning: _ => { });

        sut.IsApplicable();

        Assert.True(overrideCalled);
    }
}

// ---------------------------------------------------------------------------
// 3.1 — MlxRuntimeOptions defaults and nvfp4-firstline guard
// ---------------------------------------------------------------------------

public sealed class MlxRuntimeOptionsTests
{
    [Fact]
    public void Firstline_DefaultModelId_IsE4BOptiQ()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal("mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit", opts.ModelId);
    }

    [Fact]
    public void Firstline_DefaultPort_Is8800()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal(8800, opts.Port);
    }

    [Fact]
    public void Escalation_DefaultModelId_Is26BNvfp4()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation();

        Assert.Equal("mlx-community/gemma-4-26B-A4B-it-qat-nvfp4", opts.ModelId);
    }

    [Fact]
    public void Escalation_DefaultPort_Is8810()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation();

        Assert.Equal(8810, opts.Port);
    }

    [Fact]
    public void Firstline_And_Escalation_DefaultPorts_AreDistinct()
    {
        MlxRuntimeOptions firstline = MlxRuntimeOptions.Firstline();
        MlxRuntimeOptions escalation = MlxRuntimeOptions.Escalation();

        Assert.NotEqual(firstline.Port, escalation.Port);
    }

    [Fact]
    public void Firstline_Nvfp4ModelId_ThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => MlxRuntimeOptions.Firstline("mlx-community/gemma-4-e4b-it-qat-nvfp4"));

        Assert.Contains("nvfp4", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Firstline_Nvfp4ModelId_CaseInsensitive_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => MlxRuntimeOptions.Firstline("some-model-NVFP4-variant"));
    }

    [Fact]
    public void Firstline_NonNvfp4OverrideModelId_IsHonoured()
    {
        const string overrideModel = "mlx-community/gemma-4-e4b-it-qat-bf16";
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(overrideModel);

        Assert.Equal(overrideModel, opts.ModelId);
    }

    [Fact]
    public void Escalation_OverrideModelId_IsHonoured()
    {
        const string overrideModel = "mlx-community/gemma-4-26B-A4B-it-qat-bf16";
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation(overrideModel);

        Assert.Equal(overrideModel, opts.ModelId);
    }

    [Fact]
    public void Firstline_OverridePort_IsHonoured()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline(port: 9900);

        Assert.Equal(9900, opts.Port);
    }

    [Fact]
    public void Escalation_OverridePort_IsHonoured()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Escalation(port: 9901);

        Assert.Equal(9901, opts.Port);
    }

    [Fact]
    public void DefaultHost_Is_Loopback()
    {
        MlxRuntimeOptions opts = MlxRuntimeOptions.Firstline();

        Assert.Equal("127.0.0.1", opts.Host);
    }
}
