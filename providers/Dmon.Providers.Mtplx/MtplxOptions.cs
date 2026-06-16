namespace Dmon.Providers.Mtplx;

public sealed record MtplxOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8000;
    public string? ModelId { get; init; }
    public string? ServerPath { get; init; }
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public static MtplxOptions FromEnvironment()
    {
        string? host = Environment.GetEnvironmentVariable("MTPLX_HOST");
        string? modelId = Environment.GetEnvironmentVariable("MTPLX_MODEL_ID");
        string? serverPath = Environment.GetEnvironmentVariable("MTPLX_SERVER_PATH");

        int port = int.TryParse(Environment.GetEnvironmentVariable("MTPLX_PORT"), out int p) ? p : 8000;

        return new MtplxOptions
        {
            Host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host,
            Port = port,
            ModelId = string.IsNullOrEmpty(modelId) ? null : modelId,
            ServerPath = string.IsNullOrEmpty(serverPath) ? null : serverPath,
        };
    }
}
