using System.Text.Json;
using System.Text.Json.Serialization;
using Daemon.Protocol.Events;

namespace Daemon.Core.Rpc;

public sealed class EventEmitter : IEventEmitter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TextWriter _out;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EventEmitter(TextWriter @out)
    {
        _out = @out;
    }

    public async Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event
    {
        string json = JsonSerializer.Serialize<Event>(evt, SerializerOptions);

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
