using Dmon.Abstractions.Providers;
using Dmon.Core.Rpc;
using Dmon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Bootstrap;

public sealed class SetupCheckService
{
    private readonly IReadOnlyList<ProviderConfig> _configs;
    private readonly IReadOnlyList<IProviderFactory> _factories;
    private readonly IEventEmitter _emitter;
    private readonly ILogger<SetupCheckService> _logger;

    public SetupCheckService(
        IEnumerable<ProviderConfig> configs,
        IEnumerable<IProviderFactory> factories,
        IEventEmitter emitter,
        ILogger<SetupCheckService> logger)
    {
        _configs = configs.ToList();
        _factories = factories.ToList();
        _emitter = emitter;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_configs.Count > 0)
        {
            return;
        }

        _logger.LogDebug("No providers configured; emitting setupRequired.");

        List<AdapterInfo> adapters = _factories
            .Select(f => new AdapterInfo
            {
                Name = f.AdapterName,
                DefaultModelId = f.DefaultModelId,
                DefaultEnvVar = f.DefaultEnvVar,
                EnvVarDetected = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(f.DefaultEnvVar))
            })
            .ToList();

        await _emitter.EmitAsync(new SetupRequiredEvent
        {
            Adapters = adapters
        }, cancellationToken).ConfigureAwait(false);
    }
}
