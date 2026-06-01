using Dmon.Core.Rpc;
using Dmon.Protocol.Events;

namespace Dmon.Core.Tests.Fakes;

/// <summary>
/// Captures all emitted events so tests can assert on what was emitted.
/// </summary>
internal sealed class FakeEventEmitter : IEventEmitter
{
    private readonly List<Event> _emitted = [];

    public IReadOnlyList<Event> Emitted => _emitted;

    public void Clear() => _emitted.Clear();

    public Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
    {
        _emitted.Add(evt);
        return Task.CompletedTask;
    }
}
