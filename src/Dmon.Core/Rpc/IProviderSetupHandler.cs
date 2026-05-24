using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

public interface IProviderSetupHandler
{
    Task ConfigureAsync(ProviderConfigureCommand command, CancellationToken cancellationToken);
}
