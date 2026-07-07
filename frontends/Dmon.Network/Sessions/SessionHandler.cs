using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmon.Runtime;

namespace Dmon.Network.Sessions;

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
/// replays it. Clearing the connection also arms <c>_detachedAt</c> (if not already armed),
/// making the detected failure reap-equivalent to an orderly detach.
///
/// Outlives any single connection — attach/detach are plain method calls.
/// </summary>
public sealed class SessionHandler : IAsyncDisposable
{
    private readonly CoreSession? _coreSession;
    private readonly TextReader _stdout;
    private readonly TextWriter _stdin;
    private readonly TimeProvider _timeProvider;
    private readonly DeviceConnectionIndex _connectionIndex;

    // Guards _connection, _seqLog, _headSeq, _sentSeq, _admittedIds,
    // _isTurnInFlight, _detachedAt, and _outstanding together.
    private readonly Lock _lock = new();
    private INetworkConnection? _connection;

    // Outstanding permission requests: request-id → (OriginalSeq, RawLine).
    // Insertion order mirrors seq order (seq is monotonically increasing; a confirmRequest
    // always arrives after the seqs that precede it). Entries are removed when the matching
    // response command is successfully written to core stdin. The map's lifetime is the
    // handler's lifetime — it is never persisted, so all outstanding state is abandoned when
    // the handler is reaped (StopAsync / DisposeAsync), which is exactly the "abandoned with
    // the turn" semantic required by 8.2.
    private readonly Dictionary<string, (long OriginalSeq, string RawLine)> _outstanding = [];

    // Dedup set for inbound command ids (ADR-012 Decision 5 / Group 5).
    // Unbounded for handler lifetime — commands are user-initiated and rare relative to events.
    private readonly HashSet<string> _admittedIds = [];

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

    // True when the core has emitted turnStart without a subsequent turnEnd.
    // Written by the reader task under _lock; read by the reaper.
    private bool _isTurnInFlight;

    // Non-null while this handler has no attached connection. Set by Detach, cleared by Attach.
    // Written under _lock; the reaper reads it to determine eligibility and compute elapsed time.
    private DateTimeOffset? _detachedAt;

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

    /// <summary>
    /// The generation assigned to the most recently attached connection.
    /// Read under <c>_lock</c> so callers comparing against their own stored generation
    /// see a consistent value. Used by the forwarding loop to gate inbound frames (Group 6).
    /// </summary>
    public long CurrentGeneration
    {
        get
        {
            lock (_lock)
                return _generation;
        }
    }

    /// <summary>
    /// True when the core has emitted <c>turnStart</c> without a subsequent <c>turnEnd</c>.
    /// Read by the reaper to decide whether to apply the idle TTL or the absolute-max TTL.
    /// </summary>
    public bool IsTurnInFlight
    {
        get
        {
            lock (_lock)
                return _isTurnInFlight;
        }
    }

    /// <summary>
    /// The UTC timestamp at which this handler became detached (no attached connection).
    /// <c>null</c> when a connection is currently attached.
    /// Set by <see cref="Detach"/>, cleared by <see cref="Attach"/>.
    /// The reaper uses this to determine whether the handler has been idle long enough to reap.
    /// </summary>
    public DateTimeOffset? DetachedAt
    {
        get
        {
            lock (_lock)
                return _detachedAt;
        }
    }

    /// <param name="sessionId">Stable identifier for this session.</param>
    /// <param name="coreSession">An already-started, protocol-gated core session.</param>
    /// <param name="connectionIndex">Cross-session index for keyId → live connections.</param>
    internal SessionHandler(string sessionId, CoreSession coreSession, DeviceConnectionIndex connectionIndex)
        : this(sessionId, coreSession, connectionIndex, TimeProvider.System)
    {
    }

    /// <param name="sessionId">Stable identifier for this session.</param>
    /// <param name="coreSession">An already-started, protocol-gated core session.</param>
    /// <param name="connectionIndex">Cross-session index for keyId → live connections.</param>
    /// <param name="timeProvider">Time provider for detached-timestamp recording.</param>
    internal SessionHandler(string sessionId, CoreSession coreSession, DeviceConnectionIndex connectionIndex, TimeProvider timeProvider)
    {
        SessionId = sessionId;
        _coreSession = coreSession;
        _timeProvider = timeProvider;
        _connectionIndex = connectionIndex;
        _stdout = coreSession.Process.StandardOutput;
        _stdin = coreSession.Process.StandardInput;
        _pumpTask = RunPumpAsync(_pumpCts.Token);
    }

    /// <summary>
    /// Test seam: drives the pump from arbitrary stdout/stdin streams without spawning a process.
    /// Pass a <see cref="SessionHandlerTestOptions"/> to override streams, the shared
    /// <see cref="DeviceConnectionIndex"/>, and/or the <see cref="TimeProvider"/>.
    /// </summary>
    internal SessionHandler(string sessionId, SessionHandlerTestOptions options)
    {
        SessionId = sessionId;
        _coreSession = null;
        _timeProvider = options.TimeProvider;
        _connectionIndex = options.ConnectionIndex;
        _stdout = options.Stdout;
        _stdin = options.Stdin;
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
    ///
    /// If a prior connection is currently attached, it is evicted (fenced): its generation is
    /// superseded by the new one and its transport is aborted after the lock is released, so
    /// its in-flight <c>ReceiveAsync</c> throws and its forwarding loop exits (Group 6 / 6.3).
    /// </summary>
    public AttachResult Attach(INetworkConnection connection, long lastSeq)
    {
        long generation;
        long headSeq;
        INetworkConnection? evicted;
        lock (_lock)
        {
            // Capture the prior connection before replacing it so we can abort it outside the lock.
            evicted = _connection;

            generation = Interlocked.Increment(ref _generation);
            _connection = connection;
            // Clear the detached timestamp — this handler now has an active connection.
            _detachedAt = null;
            // Clamp: a lastSeq below 0 or above headSeq is bounded defensively.
            _sentSeq = Math.Clamp(lastSeq, 0L, _headSeq);

            // Maintain the cross-session keyId index atomically with the _connection swap:
            // add the new connection and remove the evicted one in the same lock scope so the
            // index never reflects both simultaneously and never misses either.
            if (connection.KeyId is not null)
                _connectionIndex.Add(connection.KeyId, connection);
            if (evicted is not null && evicted.KeyId is not null)
                _connectionIndex.Remove(evicted.KeyId, evicted);

            // Re-surface outstanding permission requests the client already advanced past.
            //
            // The normal replay window (cursor, headSeq] will re-deliver any outstanding request
            // whose OriginalSeq > cursor (the client never saw it). For requests with
            // OriginalSeq <= cursor the client already received them, but may have been killed
            // before answering (e.g. mobile app backgrounded). Re-emit those by appending a new
            // seq-log entry with a fresh monotonic seq above headSeq so the drain loop delivers
            // them in order after the replay window.
            //
            // Idempotency: the client keys prompts by request id; the core dedups responses by id
            // and answers a late/duplicate response with a recoverable error, so a prompt appearing
            // again under a new seq is safe. The re-emitted entry is structurally identical to the
            // original — the client must treat a re-surfaced prompt as the same request.
            foreach ((long origSeq, string rawLine) in _outstanding.Values)
            {
                if (origSeq <= _sentSeq)
                {
                    // Client advanced past this one — replay won't cover it; re-emit above headSeq.
                    _headSeq++;
                    _seqLog.Add((_headSeq, rawLine));
                }
                // origSeq > _sentSeq: replay window (cursor, headSeq] already covers it; do not
                // double-send.
            }

            headSeq = _headSeq;
        }

        // Abort the evicted connection outside the lock. Abort is synchronous and does not hold
        // shared state, so this is safe to call without the lock. The evicted connection's
        // forwarding loop will observe the abort, exit, and call Detach(evicted) — which is a
        // no-op because _connection no longer references it (Group 6 / 6.3).
        evicted?.Abort();

        // Wake the pump so it starts delivering from _sentSeq immediately.
        _wake.Release();
        return new AttachResult(generation, headSeq);
    }

    /// <summary>
    /// Detaches the given connection. Only clears <c>_connection</c> if it still references
    /// <paramref name="connection"/>; if a newer attach has already replaced it, this is a no-op.
    /// This identity guard prevents an evicted loop's cleanup from clobbering the new connection
    /// (Group 6 / 6.3). The handler and its core process remain alive; subsequent core stdout
    /// lines are buffered for the next attach.
    ///
    /// The index removal is NOT guarded by the identity check: every connection added to the
    /// index at its own Attach must be removed when its forwarding loop exits and calls
    /// Detach(itself). The Attach path already removed the evicted connection from the index
    /// atomically, so Remove is idempotent and the evicted loop's Detach call is a safe no-op.
    /// </summary>
    public void Detach(INetworkConnection connection)
    {
        // Remove from the cross-session index unconditionally — independent of the identity guard
        // below. Each Attach adds the connection; each Detach (whether current or evicted) removes
        // it. DeviceConnectionIndex.Remove is idempotent so double-removal is safe.
        if (connection.KeyId is not null)
            _connectionIndex.Remove(connection.KeyId, connection);

        lock (_lock)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                // Record when the handler first became detached so the reaper can compute
                // how long it has been idle. Only set on the first detach after an attach;
                // subsequent calls on an already-detached handler are identity-guarded above.
                _detachedAt ??= _timeProvider.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// True when there is at least one permission request (tool.confirmRequest or
    /// ui.inputRequest) whose response has not yet been written to core stdin. Used by tests to
    /// assert 8.1 (gate held while detached) and to verify 8.2 cleanup.
    /// </summary>
    public bool HasOutstandingRequests
    {
        get
        {
            lock (_lock)
                return _outstanding.Count > 0;
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
    // Command idempotency (ADR-012 Decision 5 / Group 5)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records <paramref name="id"/> as admitted and returns <see cref="CommandAdmission.Accepted"/>,
    /// or returns <see cref="CommandAdmission.Duplicate"/> if the id was already admitted.
    ///
    /// Lock discipline: the set check and write are performed under <c>_lock</c> so the first
    /// concurrent arrival records the id before any second arrival can observe it. The actual
    /// core write (<see cref="WriteToCoreAsync"/>) happens outside the lock (no await under lock).
    /// Because the id is recorded before the write returns, a duplicate that arrives while the
    /// first write is in-flight is correctly identified as a duplicate.
    /// </summary>
    public CommandAdmission TryAdmitCommand(string id)
    {
        lock (_lock)
        {
            if (!_admittedIds.Add(id))
                return CommandAdmission.Duplicate;
            return CommandAdmission.Accepted;
        }
    }

    /// <summary>
    /// Removes a previously-admitted id, compensating an admission whose subsequent core write
    /// failed. Admission is recorded before the write so a concurrent in-flight duplicate is
    /// caught; if the write then throws, the id must be un-recorded so the client's resend is
    /// re-forwarded rather than silently dropped as a duplicate (ADR-012 Decision 5).
    /// </summary>
    public void RemoveAdmission(string id)
    {
        lock (_lock)
        {
            _admittedIds.Remove(id);
        }
    }

    /// <summary>
    /// Clears the outstanding-request entry for the given request id after the matching
    /// permission-response command (tool.confirmResponse / ui.inputResponse) has been
    /// successfully written to core stdin. Called by the endpoint only on a successful write,
    /// so a failed write leaves the entry intact for re-surfacing on the next reattach.
    /// </summary>
    public void ClearOutstandingRequest(string id)
    {
        lock (_lock)
        {
            _outstanding.Remove(id);
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

                    // Passive inspection for turnStart/turnEnd (ADR-003 / Group 7) and
                    // permission-request events (Group 8). The bytes are forwarded unchanged;
                    // this pass is purely for the in-flight flag and outstanding-request map.
                    //
                    // turnStart…turnEnd brackets the whole agentic loop (including all LLM
                    // iterations and tool calls) with no inner gaps. Defensive: a second
                    // turnStart without an intervening turnEnd is idempotent (stays in-flight).
                    // The case "turn ends without a turnEnd" (e.g. core crash) is covered by
                    // RunningTurnTtlMinutes.
                    string? eventType = GetEventTypeDiscriminator(line);
                    if (eventType == "turnStart")
                        _isTurnInFlight = true;
                    else if (eventType == "turnEnd")
                        _isTurnInFlight = false;

                    // Track outstanding permission gates (8.1 / 8.2). The request id is the
                    // top-level "id" field (same field the endpoint reads for command dedup).
                    // If the same id is emitted twice (defensive), the later one wins — that
                    // matches the core's own last-write-wins dedup on response commands.
                    if (eventType is "tool.confirmRequest" or "ui.inputRequest")
                    {
                        string? requestId = GetEventId(line);
                        if (requestId is not null)
                            _outstanding[requestId] = (_headSeq, line);
                    }
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
    /// Returns the top-level "type" discriminator of an ADR-003 event line, or <c>null</c> if
    /// absent or the line is not valid JSON. Mirrors <c>GetGwDiscriminator</c> in
    /// <c>ControlFrameSerializer</c> but reads the "type" field instead of "gw". Called only
    /// from within the reader task; kept private because it is an implementation detail of
    /// the in-flight inspection pass.
    /// </summary>
    private static string? GetEventTypeDiscriminator(string line)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(line);
            if (node?["type"] is not JsonValue value)
                return null;
            return value.TryGetValue(out string? text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the top-level "id" field of an ADR-003 event line, or <c>null</c> if absent or
    /// the line is not valid JSON. Used to key outstanding permission-request entries (Group 8).
    /// Reads the same "id" field that the endpoint reads for command dedup — the core assigns
    /// the same id to the request event and its matching response command.
    /// </summary>
    private static string? GetEventId(string line)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(line);
            if (node?["id"] is not JsonValue value)
                return null;
            return value.TryGetValue(out string? text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends every retained event with seq greater than <c>_sentSeq</c> to the currently-attached
    /// connection, in seq order, advancing the cursor only after a successful send. This is the
    /// single drain path — replay of missed events and delivery of live events share the same
    /// loop so ordering is guaranteed and deduplication by seq is structural.
    ///
    /// On send failure the cursor is not advanced and the connection is cleared; the entry stays
    /// in the retained log so the next attach replays it. Clearing the connection also arms the
    /// grace clock (<c>_detachedAt</c>), making the failure reap-equivalent to an orderly detach.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            INetworkConnection? current;
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
                // is replayed from this seq on the next attach, and arm the grace clock so this
                // detected disconnect is reap-equivalent to an orderly Detach (ADR-012 Decision
                // 7/8). `??=` keeps this idempotent with the endpoint's subsequent
                // `finally -> Detach(current)`, which will find _connection already null and no-op.
                lock (_lock)
                {
                    if (ReferenceEquals(_connection, current))
                    {
                        _connection = null;
                        _detachedAt ??= _timeProvider.GetUtcNow();
                    }
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

/// <summary>
/// Collaborator bag for the <see cref="SessionHandler(string,SessionHandlerTestOptions)"/>
/// test constructor. Every member defaults to a safe no-op value; tests override only what
/// the exercised path actually touches.
///
/// <see cref="ConnectionIndex"/> intentionally has no cross-test shared default — each record
/// instance gets its own fresh index. Cross-session tests that need to verify shared key
/// tracking must pass an explicit shared instance:
/// <c>new SessionHandlerTestOptions { ConnectionIndex = sharedIndex, ... }</c>.
/// </summary>
internal sealed record SessionHandlerTestOptions
{
    public TextReader Stdout { get; init; } = TextReader.Null;
    public TextWriter Stdin { get; init; } = TextWriter.Null;
    public DeviceConnectionIndex ConnectionIndex { get; init; } = new();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
