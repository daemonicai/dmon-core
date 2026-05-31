using System.IO;
using Dmon.Runtime;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// Owns one dmoncore process for one <see cref="SessionId"/>.
///
/// Event sequencing (ADR-014): every line read from core stdout is assigned a strictly
/// monotonic per-session <c>seq</c> (starting at 1) and appended to a retained seq-indexed
/// log. Entries are never removed; the full log is available for replay.
///
/// Delivery cursor: each call to <see cref="Attach"/> supplies a <c>lastSeq</c>. The single
/// drain loop sends every retained entry whose seq is greater than the connection's cursor
/// (<c>_sentSeq</c>) up to the current <see cref="HeadSeq"/>, in order, advancing the cursor
/// only after a successful send. Live events extend the log and wake the drain loop via the
/// same <c>_wake</c> semaphore; the loop picks them up with the same <c>seq &gt; _sentSeq</c>
/// predicate. This single-loop-over-(cursor, head] design gives subscribe-then-replay with
/// deduplication by seq for free: the cursor advances monotonically and the loop never sends a
/// seq it has already delivered, so an event arriving mid-replay is delivered exactly once and
/// in order without a separate side-buffer or merge step.
///
/// Send failure: if <c>SendAsync</c> throws, <c>_sentSeq</c> is not advanced and
/// <c>_connection</c> is cleared. The event stays in the retained log so the next attach
/// replays it.
///
/// Outlives any single connection — attach/detach are plain method calls.
/// </summary>
public sealed class SessionHandler : IAsyncDisposable
{
    private readonly CoreSession? _coreSession;
    private readonly TextReader _stdout;
    private readonly TextWriter _stdin;

    // Guards _connection, _seqLog, _headSeq, and _sentSeq together.
    private readonly Lock _lock = new();
    private IGatewayConnection? _connection;

    // Retained seq-indexed event log. Entry at index i has seq i+1 (1-based), so seq N is at
    // index N-1. Never truncated in V1 (ADR-014 Decision 3; compaction is deferred).
    private readonly List<(long Seq, string Line)> _seqLog = [];

    // Highest seq assigned. Owned by the lock; read via HeadSeq property.
    private long _headSeq;

    // Highest seq successfully delivered to the current connection. Written by two paths —
    // Attach (resets it for a new connection) and the drain loop (advances it after a successful
    // send). Both writes are guarded by _lock, and the drain loop advances it only when the
    // connection it is serving is still the current one, so the two writers never lost-update
    // each other and the value is always consistent with _connection.
    private long _sentSeq;

    // Serializes writes to core stdin: StreamWriter is not thread-safe.
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    // Wakes the pump when a connection attaches or a new event arrives.
    private readonly SemaphoreSlim _wake = new(0);

    private readonly Task _pumpTask;
    private readonly CancellationTokenSource _pumpCts = new();

    // Monotonically increasing; incremented on each Attach. Group 6 enforces fencing.
    private long _generation;

    public string SessionId { get; }

    /// <summary>
    /// Highest sequence number assigned to a server→client event in this session.
    /// Zero until the first event is received from core.
    /// </summary>
    public long HeadSeq
    {
        get
        {
            lock (_lock)
                return _headSeq;
        }
    }

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
    /// Attaches a connection and sets the delivery cursor to
    /// <c>clamp(lastSeq, 0, HeadSeq)</c>. The single pump loop will replay retained events
    /// with seq greater than the cursor before delivering any newer live events, providing
    /// subscribe-then-replay ordering with deduplication by seq.
    /// Returns the new generation and the current <see cref="HeadSeq"/> atomically so the
    /// caller can send an accurate <c>attached</c> reply.
    /// Group 6 uses the generation for fencing; this method only issues it.
    /// </summary>
    public AttachResult Attach(IGatewayConnection connection, long lastSeq)
    {
        long generation;
        long headSeq;
        lock (_lock)
        {
            generation = Interlocked.Increment(ref _generation);
            _connection = connection;
            // Clamp: a lastSeq below 0 or above headSeq is bounded defensively.
            _sentSeq = Math.Clamp(lastSeq, 0L, _headSeq);
            headSeq = _headSeq;
        }

        // Wake the pump so it starts delivering from _sentSeq immediately.
        _wake.Release();
        return new AttachResult(generation, headSeq);
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
        // A dedicated reader assigns seq to each line and appends it to the retained log,
        // then wakes the drain loop. The drain loop is the sole writer to the connection.
        Task readerTask = RunReaderAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                await DrainAsync(cancellationToken).ConfigureAwait(false);
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
                    _headSeq++;
                    _seqLog.Add((_headSeq, line));
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
    /// Sends every retained event with seq greater than <c>_sentSeq</c> to the currently-attached
    /// connection, in seq order, advancing the cursor only after a successful send. This is the
    /// single drain path — replay of missed events and delivery of live events share the same
    /// loop so ordering is guaranteed and deduplication by seq is structural.
    ///
    /// On send failure the cursor is not advanced and the connection is cleared; the entry stays
    /// in the retained log so the next attach replays it.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            IGatewayConnection? current;
            long nextSeq;
            string line;
            lock (_lock)
            {
                current = _connection;
                if (current is null)
                    return;

                nextSeq = _sentSeq + 1;
                // _seqLog is 1-based: seq N is at index N-1.
                if (nextSeq > _headSeq)
                    return;

                (long seq, line) = _seqLog[(int)(nextSeq - 1)];
                // Defensive: seq must match index (invariant, never violated in normal flow).
                if (seq != nextSeq)
                    return;
            }

            try
            {
                await current.SendAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Send failed: do not advance the cursor. Clear the connection so the event
                // is replayed from this seq on the next attach.
                lock (_lock)
                {
                    if (ReferenceEquals(_connection, current))
                        _connection = null;
                }
                return;
            }

            // Advance the cursor under the lock, conditionally. Both this loop and Attach write
            // _sentSeq, so an unsynchronised increment would race: a re-attach during the await
            // above resets _sentSeq for the new connection, and a blind write here would rewind
            // its cursor (lost update + memory-visibility hole). We re-check that the connection
            // we just sent to is still the current one before advancing. If a re-attach happened,
            // _connection no longer references `current`, so we leave the new cursor untouched and
            // stop draining to the now-stale connection. (Group 6 reuses this still-current check
            // for outbound generation fencing.)
            lock (_lock)
            {
                if (!ReferenceEquals(_connection, current))
                    return;
                _sentSeq = nextSeq;
            }
        }
    }
}
