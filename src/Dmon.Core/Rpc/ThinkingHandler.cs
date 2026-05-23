using Dmon.Core.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class ThinkingHandler : IThinkingHandler
{
    private readonly IProviderRegistry _providers;
    private readonly IEventEmitter _emitter;

    private ThinkingLevel _currentLevel = ThinkingLevel.Off;

    public ThinkingLevel CurrentLevel => _currentLevel;

    public ThinkingHandler(IProviderRegistry providers, IEventEmitter emitter)
    {
        _providers = providers;
        _emitter = emitter;
    }

    public async Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Level != ThinkingLevel.Off && !_providers.CurrentSupportsReasoning)
        {
            await _emitter.EmitAsync(new CapabilityIgnoredEvent
            {
                Capability = "thinking",
                RequestedValue = cmd.Level.ToString().ToLowerInvariant(),
                Reason = "Provider does not support reasoning."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _currentLevel = cmd.Level;
    }

    public async Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken)
    {
        ThinkingLevel next = _currentLevel switch
        {
            ThinkingLevel.Off => ThinkingLevel.Low,
            ThinkingLevel.Low => ThinkingLevel.Medium,
            ThinkingLevel.Medium => ThinkingLevel.High,
            ThinkingLevel.High => ThinkingLevel.Off,
            _ => ThinkingLevel.Off
        };

        if (next != ThinkingLevel.Off && !_providers.CurrentSupportsReasoning)
        {
            await _emitter.EmitAsync(new CapabilityIgnoredEvent
            {
                Capability = "thinking",
                RequestedValue = next.ToString().ToLowerInvariant(),
                Reason = "Provider does not support reasoning."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _currentLevel = next;
    }
}
