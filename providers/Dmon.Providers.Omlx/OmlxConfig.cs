namespace Dmon.Providers.Omlx;

public sealed record OmlxConfig
{
    public string BaseUrl { get; init; } = "http://localhost:8666";
    public string ApiKey { get; init; } = string.Empty;

    public static OmlxConfig FromEnvironment()
    {
        string baseUrl = Environment.GetEnvironmentVariable("OMLX_BASE_URL") ?? "http://localhost:8666";
        string apiKey = Environment.GetEnvironmentVariable("OMLX_API_KEY") ?? string.Empty;
        return new OmlxConfig { BaseUrl = baseUrl, ApiKey = apiKey };
    }
}
