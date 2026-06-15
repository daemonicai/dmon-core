using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class EventEmitter : IEventEmitter
{
    private readonly TextWriter _out;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EventEmitter(TextWriter @out)
    {
        _out = @out;
    }

    public async Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
    {
        string json = JsonSerializer.Serialize<Event>(evt, WireSerializerOptions.Default);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _out.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _out.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
