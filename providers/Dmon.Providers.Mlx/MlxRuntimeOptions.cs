namespace Dmon.Providers.Mlx;

/// <summary>
/// Configuration for a single mlx_lm runtime (one model, one fixed port, one process handle).
/// Use <see cref="Firstline"/> or <see cref="Escalation"/> to get validated defaults.
/// </summary>
public sealed record MlxRuntimeOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8800;
    public string ModelId { get; init; } = string.Empty;
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Idle window after which the daemon may shut down this runtime to reclaim VRAM.
    /// Carried as config now; consumed by the daemon scheduler in a later change.
    /// </summary>
    public TimeSpan IdleWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Returns default options for the first-line (E4B) runtime.
    /// </summary>
    /// <param name="modelId">
    /// Override the default model id. Must not contain "nvfp4" — nvfp4 quantisation
    /// produces tool-call corruption at E4B scale and is rejected as the first-line default.
    /// </param>
    /// <param name="port">Override the default port (8800).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="modelId"/> contains "nvfp4".
    /// </exception>
    public static MlxRuntimeOptions Firstline(string? modelId = null, int port = 8800)
    {
        string effectiveModelId = modelId ?? "mlx-community/gemma-4-e4b-it-qat-OptiQ-4bit";
        if (effectiveModelId.Contains("nvfp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"nvfp4 quantisation is not suitable for the first-line runtime — " +
                $"over-quantisation at E4B scale causes tool-call corruption. " +
                $"Use a higher-quality quant for the first-line runtime. " +
                $"Rejected model: '{effectiveModelId}'.",
                nameof(modelId));

        return new MlxRuntimeOptions { Port = port, ModelId = effectiveModelId };
    }

    /// <summary>
    /// Returns default options for the escalation (26B) runtime.
    /// nvfp4 is acceptable at 26B scale and is the verified default.
    /// </summary>
    /// <param name="modelId">Override the default model id.</param>
    /// <param name="port">Override the default port (8810).</param>
    public static MlxRuntimeOptions Escalation(string? modelId = null, int port = 8810)
    {
        return new MlxRuntimeOptions
        {
            Port = port,
            ModelId = modelId ?? "mlx-community/gemma-4-26B-A4B-it-qat-nvfp4",
        };
    }
}
