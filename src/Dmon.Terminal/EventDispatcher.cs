using System.Text.Json;
using System.Threading.Channels;
using Dmon.Protocol.Events;

namespace Dmon.Terminal;

/// <summary>
/// Reads JSONL events from the core process's standard output and dispatches
/// them onto a channel for the main loop to consume.
/// </summary>
public sealed class EventDispatcher
{
    private readonly StreamReader _reader;
    private readonly Channel<Event> _channel;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChannelReader<Event> Events => _channel.Reader;

    public EventDispatcher(StreamReader reader)
    {
        _reader = reader;
        _channel = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Runs the dispatch loop until the stream closes or cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                line = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    Event? evt = JsonSerializer.Deserialize<Event>(line, JsonOptions);
                    if (evt is not null)
                        await _channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    await System.Console.Error.WriteLineAsync(
                        $"[terminal] Failed to deserialize event: {ex.Message}").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}
