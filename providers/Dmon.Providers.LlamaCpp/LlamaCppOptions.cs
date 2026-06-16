namespace Dmon.Providers.LlamaCpp;

public sealed record LlamaCppOptions
{
    public required string ModelId { get; init; }
    public string Quant { get; init; } = "Q4_K_M";
    public string? ServerPath { get; init; }
    public int? Port { get; init; }
    public int? ContextSize { get; init; }
    public int? GpuLayers { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public string Host { get; init; } = "127.0.0.1";

    public static LlamaCppOptions FromEnvironment()
    {
        string modelId = Environment.GetEnvironmentVariable("LLAMA_MODEL_ID") ?? string.Empty;
        string quant = Environment.GetEnvironmentVariable("LLAMA_QUANT") ?? "Q4_K_M";
        string? serverPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH");
        string? host = Environment.GetEnvironmentVariable("LLAMA_HOST");

        int? port = int.TryParse(Environment.GetEnvironmentVariable("LLAMA_PORT"), out int p) ? p : null;
        int? contextSize = int.TryParse(Environment.GetEnvironmentVariable("LLAMA_CONTEXT_SIZE"), out int cs) ? cs : null;
        int? gpuLayers = int.TryParse(Environment.GetEnvironmentVariable("LLAMA_GPU_LAYERS"), out int gl) ? gl : null;

        return new LlamaCppOptions
        {
            ModelId = modelId,
            Quant = quant,
            ServerPath = serverPath,
            Port = port,
            ContextSize = contextSize,
            GpuLayers = gpuLayers,
            Host = host ?? "127.0.0.1",
        };
    }
}
