using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

/// <summary>
/// Placeholder extension handler — full implementation is in a later group.
/// </summary>
internal sealed class NullExtensionHandler : IExtensionHandler
{
    public Task LoadAsync(ExtensionLoadCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("extension.load not yet implemented");

    public Task UnloadAsync(ExtensionUnloadCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("extension.unload not yet implemented");

    public Task PromoteAsync(ExtensionPromoteCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("extension.promote not yet implemented");
}
