using System.Runtime.CompilerServices;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Runtime;

/// <summary>
/// Default <see cref="IRpcTransport"/> implementation backed by an <see cref="ICoreProcess"/>.
/// Reads events from <see cref="ICoreProcess.StandardOutput"/> and writes commands to
/// <see cref="ICoreProcess.StandardInput"/>.
/// </summary>
public sealed class CoreProcessRpcTransport : IRpcTransport, IDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly Action<string>? _onParseError;

    // Serializes concurrent SendAsync callers so frames from different callers are never
    // interleaved on the wire (D1). Never guards _reader — the read path is a single pump.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Initialises a transport over an already-started <see cref="ICoreProcess"/>.
    /// </summary>
    /// <param name="process">The running core process whose stdio pipes to use.</param>
    /// <param name="onParseError">
    /// Optional callback invoked when a line cannot be deserialized.
    /// The argument is a diagnostic message — it is never written to the event stream.
    /// Defaults to no-op.
    /// </param>
    public CoreProcessRpcTransport(ICoreProcess process, Action<string>? onParseError = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _reader = process.StandardOutput;
        _writer = process.StandardInput;
        _onParseError = onParseError;
    }

    /// <summary>
    /// Initialises a transport over explicit <see cref="TextReader"/> / <see cref="TextWriter"/>
    /// pairs. Intended for unit tests.
    /// </summary>
    /// <param name="reader">The source of JSONL event lines.</param>
    /// <param name="writer">The sink for JSONL command lines.</param>
    /// <param name="onParseError">Optional parse-error diagnostic callback.</param>
    internal CoreProcessRpcTransport(
        TextReader reader,
        TextWriter writer,
        Action<string>? onParseError = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        _reader = reader;
        _writer = writer;
        _onParseError = onParseError;
    }

    /// <inheritdoc/>
    public async Task SendAsync(Command command, CancellationToken cancellationToken)
    {
        // Serialize through the base Command type so the "type" discriminator is emitted.
        // Serialization touches no shared state, so it happens outside the write gate (D2).
        string json = JsonSerializer.Serialize(command, WireSerializerOptions.Default);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Exactly one LF, never CR, written as a single atomic frame so concurrent
            // callers can never interleave a partial frame onto the wire (D1).
            await _writer.WriteAsync((json + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Releases the write-serialization semaphore only. The borrowed <see cref="_reader"/>
    /// and <see cref="_writer"/> streams are never owned by this transport and are never
    /// closed here (D3) — the owner of <see cref="ICoreProcess"/> or the test caller closes them.
    /// </summary>
    public void Dispose() => _writeGate.Dispose();

    /// <inheritdoc/>
    public IAsyncEnumerable<Event> Events => ReadEventsAsync(CancellationToken.None);

    private async IAsyncEnumerable<Event> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            // null means the stream has closed — complete normally, no throw.
            if (line is null)
                yield break;

            // Skip blank / whitespace-only lines (ADR-003 resilience).
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Event? evt;
            try
            {
                evt = JsonSerializer.Deserialize<Event>(line, WireSerializerOptions.Default);
            }
            catch (JsonException ex)
            {
                _onParseError?.Invoke(
                    $"dmon-transport: failed to deserialize event line: {ex.Message}");
                continue;
            }

            if (evt is null)
            {
                _onParseError?.Invoke(
                    "dmon-transport: deserializer returned null for a non-empty line.");
                continue;
            }

            yield return evt;
        }
    }
}
