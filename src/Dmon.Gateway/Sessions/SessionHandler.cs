using System.IO;
using Dmon.Runtime;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// Owns one dmoncore process for one <see cref="SessionId"/>.
/// A single pump loop is the only writer to the attached <see cref="IGatewayConnection"/>:
/// it drains the in-memory buffer in order and only then lets newer lines flow, so buffered
/// events can never be overtaken by freshly-arrived live events.
/// While detached, events accumulate in the in-memory buffer. That retention holds until a
/// connection successfully receives them; if a flush fails the undelivered tail is re-buffered
/// so it is not lost in-process. Durable cross-restart retention is Group 4 — until then the
/// buffer is volatile and a process restart discards it.
/// Outlives any single connection — attach/detach are plain method calls.
/// </summary>
public sealed class SessionHandler : IAsyncDisposable
{
    private readonly CoreSession? _coreSession;
    private readonly TextReader _stdout;
    private readonly TextWriter _stdin;

    // Guards _connection and _buffer together to avoid TOCTOU between attach/detach/pump.
    private readonly Lock _lock = new();
    private IGatewayConnection? _connection;
    private readonly List<string> _buffer = [];

    // Serializes writes to core stdin: StreamWriter is not thread-safe.
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    // Wakes the pump when a connection attaches so buffered lines flush even if stdout is idle.
    private readonly SemaphoreSlim _wake = new(0);

    private readonly Task _pumpTask;
    private readonly CancellationTokenSource _pumpCts = new();

    // Monotonically increasing; incremented on each Attach. Group 6 enforces fencing.
    private long _generation;

    public string SessionId { get; }

    /// <summary>
    /// Placeholder for the highest persisted sequence number. Group 4 wires the real value
    /// from messages.jsonl; until then this is always 0.
    /// </summary>
    public long HeadSeq => 0;

    /// <param name="sessionId">Stable identifier for this session.</param>
    /// <param name="coreSession">An already-started, protocol-gated core session.</param>
    public SessionHandler(string sessionId, CoreSession coreSession)
    {
        SessionId = sessionId;
        _coreSession = coreSession;
        _stdout = coreSession.Process.StandardOutput;
        _stdin = coreSession.Process.StandardInput;
        _pumpTask = RunPumpAsync(_pumpCts.Token);
    }

    /// <summary>
    /// Test seam: drives the pump from arbitrary stdout/stdin streams without spawning a process.
    /// </summary>
    internal SessionHandler(string sessionId, TextReader stdout, TextWriter stdin)
    {
        SessionId = sessionId;
        _coreSession = null;
        _stdout = stdout;
        _stdin = stdin;
        _pumpTask = RunPumpAsync(_pumpCts.Token);
    }

    // -------------------------------------------------------------------------
    // Attach / detach
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attaches a connection. Buffered events are flushed by the single pump loop, in original
    /// order, before any later line is delivered. Replaces any previously attached connection.
    /// Returns the new monotonic generation number for the <c>attached</c> reply.
    /// Group 6 uses the generation for fencing; this method only issues it.
    /// </summary>
    public long Attach(IGatewayConnection connection)
    {
        long generation;
        lock (_lock)
        {
            generation = Interlocked.Increment(ref _generation);
            _connection = connection;
        }

        // Wake the pump so it drains the buffer to the new connection immediately.
        _wake.Release();
        return generation;
    }

    /// <summary>
    /// Detaches the current connection. The handler and its core process remain alive;
    /// subsequent core stdout lines are buffered for the next attach.
    /// </summary>
    public void Detach()
    {
        lock (_lock)
        {
            _connection = null;
        }
    }

    // -------------------------------------------------------------------------
    // Client → core
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes a raw command JSON line to core stdin with strict LF framing (ADR-003).
    /// Writes are serialized because <see cref="StreamWriter"/> is not thread-safe; the frame
    /// and its terminating LF are emitted in a single write so framing cannot interleave.
    /// </summary>
    public async Task WriteToCoreAsync(string frameJson, CancellationToken cancellationToken)
    {
        await _stdinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync((frameJson + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Shutdown
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stops the core process and completes the pump task cleanly.
    /// </summary>
    public async Task StopAsync()
    {
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        // Unblock the pump if it is parked waiting for a wake signal.
        _wake.Release();
        if (_coreSession is not null)
            await _coreSession.Process.StopAsync().ConfigureAwait(false);

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _coreSession?.Process.Dispose();
        _pumpCts.Dispose();
        _stdinLock.Dispose();
        _wake.Dispose();
    }

    // -------------------------------------------------------------------------
    // Stdout pump — the single writer to the attached connection
    // -------------------------------------------------------------------------

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        // A dedicated reader appends stdout lines to the buffer and wakes the drain loop.
        // The drain loop below is the sole writer to the connection, so it is the only place
        // ordering decisions are made: buffered lines always precede later ones.
        Task readerTask = RunReaderAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                await DrainBufferAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    private async Task RunReaderAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                // Strip any stray CR so framing is always pure LF (ADR-003).
                line = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                lock (_lock)
                {
                    _buffer.Add(line);
                }

                // Wake the drain loop; it will deliver to the connection if one is attached.
                _wake.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Drains buffered lines to the currently-attached connection, in order. This is the only
    /// path that writes event frames to a connection, which guarantees buffered-before-live
    /// ordering. On a send failure the undelivered tail (including the failed line) is left at
    /// the front of the buffer and the connection is detached, so events stay retained in memory
    /// until a successful reattach.
    /// </summary>
    private async Task DrainBufferAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            IGatewayConnection? current;
            string line;
            lock (_lock)
            {
                current = _connection;
                if (current is null || _buffer.Count == 0)
                    return;
                line = _buffer[0];
            }

            try
            {
                await current.SendAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Send failed: keep the line (and everything after it) buffered and detach the
                // connection so the next attach replays from here in order.
                lock (_lock)
                {
                    if (ReferenceEquals(_connection, current))
                        _connection = null;
                }
                return;
            }

            lock (_lock)
            {
                // Remove the delivered head only if it is still the line we just sent. A
                // concurrent path never removes from the buffer, so this is the only remover.
                if (_buffer.Count > 0 && ReferenceEquals(_buffer[0], line))
                    _buffer.RemoveAt(0);
            }
        }
    }
}
