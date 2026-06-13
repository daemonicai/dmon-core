using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Dmon.Gateway;
using Dmon.Gateway.DeviceKeys;
using Dmon.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Task 8.1 — End-to-end test: the full empty → pair → revoke lifecycle in a single
/// test sequence reusing the SAME provider / index / watcher / endpoint instances
/// throughout (no restart across phases).
///
/// Phases:
///   1. Empty set → auth disabled; any connection is authorized.
///   2. Pair a device (file write + watcher.Reload()) → only the matching token
///      passes auth; wrong token and missing header → 401.
///   3. Revoke k1 (file write with revokedAt set, k2 still active + watcher.Reload()):
///      - new connect with Bearer token1 → 401 (k1 left the active set).
///      - LIVE connection that was attached as k1 is fenced: Abort() is called by the
///        watcher; the forwarding loop exits; Detach removes the entry from the index.
///
/// Determinism: <c>watcher.Reload()</c> is driven directly instead of relying on
/// <see cref="FileSystemWatcher"/> event timing (the async FSW path is covered by
/// group 4). This exercises the SAME reload+diff+fence+swap code path without races.
/// </summary>
public sealed class PerDeviceKeyE2ETests : IDisposable
{
    private readonly string _tempDir;

    public PerDeviceKeyE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // =========================================================================
    // 8.1 — Main e2e sequence
    // =========================================================================

    /// <summary>
    /// Full empty → pair → revoke sequence using the same provider / index / watcher /
    /// endpoint instances throughout — no restart, no component reconstruction.
    ///
    ///   Phase 1: Empty set → auth disabled.
    ///   Phase 2: Non-empty set (k1) → correct token passes, wrong token → 401,
    ///            missing header → 401; live connection is registered in the index.
    ///   Phase 3: k1 revoked (revokedAt set; k2 added to keep set non-empty) →
    ///            new Bearer token1 → 401; live k1 connection fenced and loop exits;
    ///            index.GetConnections("k1") empty after loop exit → Detach.
    /// </summary>
    [Fact]
    public async Task EmptyToPairToRevoke_NoRestartAcrossSequence()
    {
        // -----------------------------------------------------------------------
        // Shared infrastructure — ONE instance of each, never recreated.
        // These are the objects the test asserts are reused (the "no restart" property).
        // -----------------------------------------------------------------------
        string devicesPath = Path.Combine(_tempDir, "devices.json");
        string lastSeenPath = Path.Combine(_tempDir, "lastseen.json");

        DeviceConnectionIndex index = new();
        DeviceKeySetProvider provider = new(DeviceKeySet.Empty);

        GatewayDeviceKeyPaths paths = new(
            DevicesPath: devicesPath,
            LastSeenPath: lastSeenPath);

        DeviceKeyStoreWatcher watcher = new(
            provider,
            index,
            paths,
            NullLogger<DeviceKeyStoreWatcher>.Instance);

        SessionRegistry registry = new();

        // Endpoint is wired to the SAME provider and index for all three phases.
        GatewayConnectionEndpoint endpoint = new(
            registry,
            new GatewayConnectionEndpoint.TestOptions
            {
                DeviceKeySetProvider = provider,
                ConnectionIndex = index,
            },
            NullLogger<GatewayConnectionEndpoint>.Instance);

        // -----------------------------------------------------------------------
        // Phase 1: Empty set → auth disabled.
        // -----------------------------------------------------------------------

        Assert.True(provider.Current.IsEmpty, "Phase 1: provider must start empty");

        // No Authorization header — must NOT be rejected (auth disabled).
        DefaultHttpContext ctx1a = new();
        await endpoint.HandleAsync(ctx1a);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx1a.Response.StatusCode);

        // Arbitrary token — must also pass (auth disabled; all connections authorized).
        DefaultHttpContext ctx1b = new();
        ctx1b.Request.Headers.Authorization = "Bearer unknown-token";
        await endpoint.HandleAsync(ctx1b);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx1b.Response.StatusCode);

        // -----------------------------------------------------------------------
        // Phase 2: Pair k1 with token "token1" (file write + Reload).
        // Same provider / index / watcher / endpoint — no restart.
        // -----------------------------------------------------------------------

        File.WriteAllText(devicesPath, DevicesJson(
            DeviceEntry("k1", HashToken("token1"), revoked: false)));

        watcher.Reload(); // deterministic: no FSW timing

        Assert.False(provider.Current.IsEmpty, "Phase 2: provider must have k1 after Reload");

        // 2a. Correct token → auth passes (falls through to non-WS check → 400, not 401).
        DefaultHttpContext ctx2a = new();
        ctx2a.Request.Headers.Authorization = "Bearer token1";
        await endpoint.HandleAsync(ctx2a);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx2a.Response.StatusCode);

        // 2b. Wrong token → 401.
        DefaultHttpContext ctx2b = new();
        ctx2b.Request.Headers.Authorization = "Bearer wrongtoken";
        await endpoint.HandleAsync(ctx2b);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx2b.Response.StatusCode);

        // 2c. No header (set is non-empty) → 401.
        DefaultHttpContext ctx2c = new();
        await endpoint.HandleAsync(ctx2c);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx2c.Response.StatusCode);

        // 2d. Register a session and attach a keyed connection so the index records k1.
        string sessionId = "e2e-s-8-1";
        await using SessionHandler handler = new(
            sessionId,
            new SessionHandlerTestOptions
            {
                Stdout = new NeverReadingReader(),
                Stdin = TextWriter.Null,
                ConnectionIndex = index,
            });
        registry.Register(sessionId, handler);

        // Attach a connection tagged with keyId "k1" (simulates an authorized attach).
        KeyedAbortableConnection conn = new("k1");
        handler.Attach(conn, lastSeq: 0);

        // Index must now record conn under "k1".
        Assert.Contains(conn, index.GetConnections("k1"));

        // -----------------------------------------------------------------------
        // Phase 3: Revoke k1.
        //
        // Write devices.json with k1 carrying revokedAt (excluded from active set)
        // and k2 still active (so the set is non-empty and auth remains enabled).
        // After Reload():
        //   - k1's credential has left the active set → watcher fences live k1 connections.
        //   - provider.Current does not contain k1's secretHash → Bearer token1 → 401.
        //   - provider.Current contains k2 → Bearer token2 → passes auth.
        //
        // Live-connection fencing: the forwarding loop for conn is running on a background
        // task. When watcher.Reload() calls conn.Abort(), the AbortableFakeWebSocket cancels
        // its internal CTS; ReceiveAsync (which is blocked waiting for the next frame) throws
        // OperationCanceledException via the linked token and the loop exits.
        // -----------------------------------------------------------------------

        // Start a live forwarding loop for conn on a background task.
        // No frames are queued — the loop blocks in ReceiveAsync until aborted.
        Task liveLoopTask = endpoint.RunForwardingLoopForTestAsync(
            conn.Socket,
            conn,
            handler,
            CancellationToken.None, // outer CT; the socket's Abort() drives exit instead
            myGeneration: 1,
            enforceFencing: false); // revocation fencing is via Abort, not generation check

        // Verify the connection is live in the index before revocation.
        Assert.Contains(conn, index.GetConnections("k1"));

        // Revoke k1: write file with k1 revoked and k2 still active; then reload.
        File.WriteAllText(devicesPath, DevicesJson(
            DeviceEntry("k1", HashToken("token1"), revoked: true),
            DeviceEntry("k2", HashToken("token2"), revoked: false)));

        watcher.Reload();

        // Revocation evidence — two assertions that together prove Reload() drove Abort()
        // and the forwarding loop actually exited:
        //   1. conn.WasAborted is true  (Abort() was called synchronously by the fencing logic).
        //   2. liveLoopTask completed within the timeout  (the loop observed the fence and exited).
        // If either fails, revocation did not work; everything below is moot.
        Assert.True(conn.WasAborted, "Phase 3: live connection must be fenced by Reload");
        await WaitForTaskAsync(liveLoopTask, TimeSpan.FromSeconds(5));

        // Mirror the production HandleAsync finally block.
        // RunForwardingLoopForTestAsync has no finally, so we call Detach manually here
        // exactly as HandleAsync would once the loop exits.  The index-empty assertion
        // below verifies the group-5 Detach→cleanup path runs correctly; it does NOT
        // prove revocation (the two assertions above already did that).
        handler.Detach(conn);
        Assert.Empty(index.GetConnections("k1"));

        // New connection attempt with the revoked token → 401.
        // (The active set no longer contains k1's secretHash.)
        DefaultHttpContext ctx3a = new();
        ctx3a.Request.Headers.Authorization = "Bearer token1";
        await endpoint.HandleAsync(ctx3a);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx3a.Response.StatusCode);

        // k2 is still active → its token passes auth (falls through to non-WS → 400 not 401).
        DefaultHttpContext ctx3b = new();
        ctx3b.Request.Headers.Authorization = "Bearer token2";
        await endpoint.HandleAsync(ctx3b);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx3b.Response.StatusCode);

        // The set is non-empty (k2 is active) → no header → 401 (auth still enabled).
        DefaultHttpContext ctx3c = new();
        await endpoint.HandleAsync(ctx3c);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx3c.Response.StatusCode);
    }

    // =========================================================================
    // JSON helpers
    // =========================================================================

    private static string DeviceEntry(string keyId, string secretHash, bool revoked) =>
        revoked
            ? $$$"""{"keyId":"{{{keyId}}}","name":"{{{keyId}}}","secretHash":"{{{secretHash}}}","createdAt":"2024-01-01T00:00:00Z","revokedAt":"2024-06-01T00:00:00Z"}"""
            : $$$"""{"keyId":"{{{keyId}}}","name":"{{{keyId}}}","secretHash":"{{{secretHash}}}","createdAt":"2024-01-01T00:00:00Z"}""";

    private static string DevicesJson(params string[] deviceEntries) =>
        $$"""{"schemaVersion":1,"devices":[{{string.Join(",", deviceEntries)}}]}""";

    /// <summary>
    /// Returns the lowercase-hex SHA-256 of <paramref name="token"/> — the stored form.
    /// Matches the helper in <c>AuthAndBindTests</c> and <c>GatewayIntegrationTests</c>.
    /// </summary>
    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // =========================================================================
    // Async helpers — deterministic, no fixed sleeps
    // =========================================================================

    private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            throw new TimeoutException(
                $"Forwarding loop did not exit within {timeout}. " +
                $"Task status: {task.Status}");
        }
    }

    // =========================================================================
    // Fakes
    // =========================================================================

    /// <summary>
    /// A <see cref="WebSocket"/> whose <see cref="Abort"/> cancels an internal
    /// <see cref="CancellationTokenSource"/>. <see cref="ReceiveAsync"/> links both the
    /// caller's token and the internal abort token so that either signal unblocks the wait.
    ///
    /// This models production behaviour: on a real socket, <see cref="WebSocket.Abort"/>
    /// causes any blocked <c>ReceiveAsync</c> to throw immediately. The
    /// <see cref="FakeClientWebSocket"/> in <c>GatewayIntegrationTests</c> sets state to
    /// <c>Aborted</c> but does not unblock the channel read; this variant does both.
    /// </summary>
    private sealed class AbortableFakeWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Payload)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

        private readonly CancellationTokenSource _abortCts = new();
        private WebSocketState _state = WebSocketState.Open;
        private bool _aborted;
        private readonly Lock _stateLock = new();

        public bool WasAborted
        {
            get { lock (_stateLock) { return _aborted; } }
        }

        public void QueueClose() =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Close, []));

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // Link the caller's token with the internal abort token.
            // When Abort() is called, _abortCts is cancelled and ReadAsync unblocks.
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCts.Token);

            (WebSocketMessageType type, byte[] payload) =
                await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            int count = Math.Min(payload.Length, buffer.Count);
            Array.Copy(payload, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            lock (_stateLock) { _state = WebSocketState.Closed; }
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            lock (_stateLock) { _state = WebSocketState.CloseSent; }
            return Task.CompletedTask;
        }

        public override void Abort()
        {
            lock (_stateLock)
            {
                _aborted = true;
                _state = WebSocketState.Aborted;
            }
            // Cancel outside the lock: CancellationTokenSource.Cancel is safe to call
            // concurrently and may invoke continuations that should not run under _stateLock.
            _abortCts.Cancel();
        }

        public override void Dispose()
        {
            _abortCts.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="IGatewayConnection"/> with a non-null <see cref="KeyId"/>, backed by
    /// an <see cref="AbortableFakeWebSocket"/>. When the watcher calls <see cref="Abort"/>,
    /// the underlying socket cancels its internal CTS, unblocking the forwarding loop's
    /// blocked <c>ReceiveAsync</c>.
    /// </summary>
    private sealed class KeyedAbortableConnection(string keyId) : IGatewayConnection
    {
        public AbortableFakeWebSocket Socket { get; } = new();

        public string? KeyId { get; } = keyId;

        public bool WasAborted => Socket.WasAborted;

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Abort() => Socket.Abort();
    }

    /// <summary>
    /// Swallows <see cref="OperationCanceledException"/> and returns null (EOF),
    /// matching the contract <see cref="SessionHandler"/>'s pump loop expects from
    /// a stdout reader. Avoids the test hanging on handler disposal.
    /// </summary>
    private sealed class NeverReadingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Swallow — signals EOF to the pump.
            }

            return null;
        }

        public override Task<string?> ReadLineAsync() =>
            ReadLineAsync(CancellationToken.None).AsTask();
    }
}
