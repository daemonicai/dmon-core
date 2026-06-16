using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmon.Abstractions.Providers;

namespace Dmon.Providers.Omlx;

public sealed class OmlxProviderExtension : IProviderExtension, IDisposable
{
    // Visible for tests to verify
    internal const string OmlxOwnedBy = "omlx";

    private static readonly TimeSpan IsRunningTimeout = TimeSpan.FromSeconds(2);

    private readonly OmlxConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<bool>>? _isRunningProbe;

    public string ProviderName => "oMLX";

    public OmlxProviderExtension(OmlxConfig? config = null)
    {
        _config = config ?? OmlxConfig.FromEnvironment();
        OmlxAuthHandler authHandler = new(_config.ApiKey) { InnerHandler = new HttpClientHandler() };
        _httpClient = new HttpClient(authHandler) { BaseAddress = new Uri(_config.BaseUrl) };
    }

    // Internal constructor for testability — accepts an injected HttpClient (for IsRunningAsync + ListModelsAsync)
    internal OmlxProviderExtension(OmlxConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    // Internal constructor for testability — accepts a probe that replaces the real IsRunningAsync
    // (used only by EnsureRunningAsync tests; ListModelsAsync tests use the HttpClient overload)
    internal OmlxProviderExtension(OmlxConfig config, Func<CancellationToken, Task<bool>> isRunningProbe)
    {
        _config = config;
        _httpClient = new HttpClient();
        _isRunningProbe = isRunningProbe;
    }

    public bool IsApplicable()
    {
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunningProbe is not null)
            return await _isRunningProbe(cancellationToken).ConfigureAwait(false);

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(IsRunningTimeout);

            HttpResponseMessage response = await _httpClient
                .GetAsync("/v1/models", cts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return false;

            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            OmlxModelsResponse? result = JsonSerializer.Deserialize(body, OmlxJsonContext.Default.OmlxModelsResponse);
            return result?.Data?.Any(m => m.OwnedBy == OmlxOwnedBy) == true;
        }
        catch
        {
            return false;
        }
    }

    public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
        EnsureRunningAsync(TimeSpan.FromSeconds(30), cancellationToken);

    internal async Task EnsureRunningAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        bool running = await (_isRunningProbe is not null
            ? _isRunningProbe(cancellationToken)
            : IsRunningAsync(cancellationToken)).ConfigureAwait(false);

        if (running)
            return;

        Process.Start(new ProcessStartInfo("open", "-a oMLX") { UseShellExecute = false });

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            bool up = await (_isRunningProbe is not null
                ? _isRunningProbe(cancellationToken)
                : IsRunningAsync(cancellationToken)).ConfigureAwait(false);
            if (up)
                return;
        }

        throw new TimeoutException($"oMLX did not respond within {timeout.TotalSeconds} seconds.");
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _httpClient
                .GetAsync("/v1/models", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return [];

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            OmlxModelsResponse? result = JsonSerializer.Deserialize(body, OmlxJsonContext.Default.OmlxModelsResponse);

            if (result?.Data is null)
                return [];

            return result.Data
                .Select(m => new ModelInfo
                {
                    Id = m.Id ?? string.Empty,
                    Capabilities = OmlxCapabilityHeuristic.Infer(m.Id ?? string.Empty),
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public IProviderFactory CreateFactory() => new OmlxProviderFactory(_config);

    public void Dispose()
    {
        _httpClient.Dispose();
    }

}

// JSON model types for /v1/models response

internal sealed class OmlxModelsResponse
{
    [JsonPropertyName("data")]
    public List<OmlxModelEntry>? Data { get; set; }
}

internal sealed class OmlxModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }
}

[JsonSerializable(typeof(OmlxModelsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class OmlxJsonContext : JsonSerializerContext
{
}
