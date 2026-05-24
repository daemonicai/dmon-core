using Dmon.Core.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dmon.Core.Extensions;

public sealed class BuiltinToolsInitializer : IHostedService
{
    private readonly IToolRegistry _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IGhCliService _ghCliService;

    public BuiltinToolsInitializer(
        IToolRegistry registry,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IGhCliService ghCliService)
    {
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _ghCliService = ghCliService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        HttpClient httpClient = _httpClientFactory.CreateClient("builtin");
        int timeoutSeconds = _configuration.GetValue("Daemon:Tools:Bash:TimeoutSeconds", 30);
        _registry.AddBuiltinTools(httpClient, _ghCliService, timeoutSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
