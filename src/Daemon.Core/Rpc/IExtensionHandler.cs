using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

public interface IExtensionHandler
{
    Task LoadAsync(ExtensionLoadCommand cmd, CancellationToken cancellationToken);
    Task UnloadAsync(ExtensionUnloadCommand cmd, CancellationToken cancellationToken);
    Task PromoteAsync(ExtensionPromoteCommand cmd, CancellationToken cancellationToken);
}
