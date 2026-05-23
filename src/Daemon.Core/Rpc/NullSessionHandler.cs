using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

/// <summary>
/// Placeholder session handler — full implementation is in a later group.
/// </summary>
internal sealed class NullSessionHandler : ISessionHandler
{
    public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.create not yet implemented");

    public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.fork not yet implemented");

    public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.clone not yet implemented");

    public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.load not yet implemented");

    public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.list not yet implemented");

    public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.setName not yet implemented");

    public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.getStats not yet implemented");

    public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("session.getMessages not yet implemented");
}
