using System.Runtime.InteropServices;
using Dmon.Abstractions.Providers;

namespace Dmon.Providers.Mlx;

public sealed class MlxProviderExtension : IProviderExtension, IDisposable
{
    private readonly MlxRuntimeOptions _options;
    private readonly Func<bool>? _isMacOsOverride;
    private readonly Func<Architecture>? _osArchitectureOverride;
    private readonly Func<string?>? _resolveUvPathOverride;
    private readonly Action<string>? _onWarning;

    // Exposes parsed options for test assertions without widening production visibility.
    internal MlxRuntimeOptions Options => _options;

    public string ProviderName => "mlx";

    // Public constructor — used in production.
    public MlxProviderExtension(
        MlxRuntimeOptions options,
        Action<string>? onWarning = null)
    {
        _options = options;
        _onWarning = onWarning;
    }

    // Internal constructor for testability — injected OS/arch/uv-resolve overrides (for IsApplicable tests).
    internal MlxProviderExtension(
        MlxRuntimeOptions options,
        Func<bool> isMacOsOverride,
        Func<Architecture> osArchitectureOverride,
        Func<string?> resolveUvPathOverride,
        Action<string>? onWarning = null)
    {
        _options = options;
        _isMacOsOverride = isMacOsOverride;
        _osArchitectureOverride = osArchitectureOverride;
        _resolveUvPathOverride = resolveUvPathOverride;
        _onWarning = onWarning;
    }

    public bool IsApplicable()
    {
        bool isMacOs = (_isMacOsOverride ?? OperatingSystem.IsMacOS)();
        if (!isMacOs)
        {
            _onWarning?.Invoke(
                "MLX requires macOS on Apple Silicon. This system is not running macOS.");
            return false;
        }

        Architecture arch = (_osArchitectureOverride ?? (() => RuntimeInformation.OSArchitecture))();
        if (arch != Architecture.Arm64)
        {
            _onWarning?.Invoke(
                "MLX requires Apple Silicon (arm64). This Mac is running on a non-arm64 architecture.");
            return false;
        }

        string? uvPath = (_resolveUvPathOverride ?? FindUvOnPath)();
        if (uvPath is not null)
            return true;

        _onWarning?.Invoke(
            "uv not found on PATH. Install uv with: curl -LsSf https://astral.sh/uv/install.sh | sh. " +
            "uv is required to manage the mlx_lm Python environment.");
        return false;
    }

    public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in mlx-local-runtime tasks 3.3–3.7.");

    public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in mlx-local-runtime tasks 3.3–3.7.");

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in mlx-local-runtime tasks 3.3–3.7.");

    public IProviderFactory CreateFactory() =>
        throw new NotImplementedException("Implemented in mlx-local-runtime tasks 3.3–3.7.");

    // Dispose is a no-op in this scaffold; later blocks will close the managed process here.
    public void Dispose() { }

    // --- Private helpers ---

    private static string? FindUvOnPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;

        // macOS/arm64-only provider; Unix PATH separator, no extension probing needed.
        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "uv");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
