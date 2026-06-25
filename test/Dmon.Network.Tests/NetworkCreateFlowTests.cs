using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dmon.Core.Config;
using Dmon.Network.Protocol;
using Dmon.Network.Sessions;
using Dmon.Protocol.Gateway;
using Dmon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Network.Tests;

/// <summary>
/// Group 6: gateway create-flow tests.
///
/// 6.1 — Create with a known agent: handshake returns the session id, agent is forwarded
///        to core stdin, handler is registered, and <c>created {sessionId}</c> is sent to the client.
///        A subsequent attach to the registered session succeeds.
///
/// 6.2 — Create with an unknown agent: a typed <c>createRejected</c> is returned,
///        the registry count is unchanged, and no core is spawned
///        (<see cref="NetworkConnectionEndpoint.HandleCreateAsync"/> returns before calling
///        the core launcher). The <c>unknown_agent</c> error code is covered.
///        A null agent bypasses validation entirely.
///
/// 6.3 — Create at cap: <c>createRejected {code="cap_reached"}</c> is returned and the registry
///        count remains at the cap. Reattach to an already-registered session is exempt from the cap.
/// </summary>
public sealed class NetworkCreateFlowTests
{
    // =========================================================================
    // 6.1 — Create with a known agent
    //
    // Coverage boundary (intentional — not a gap):
    //
    // The 6.1 success path is verified through three decomposed seam tests:
    //   6.1a — DriveSessionHandshakeAsync (handshake result consumption + agent forwarding).
    //   6.1b — SessionRegistry.TryRegister + TryGet (handler registration / attach lookup).
    //   6.1c — CreatedFrame wire shape (the frame returned to the client).
    //
    // What is NOT covered by automation is the in-HandleCreateAsync ordering between these parts —
    // specifically the ADR-014-critical sequencing that DriveSessionHandshakeAsync consumes BOTH
    // handshake result lines (session.createResult + session.loadResult) BEFORE new SessionHandler(...)
    // starts the seq-assigning stdout pump. A full HandleCreateAsync end-to-end test would need to
    // launch a real (or fake) OS process via an ICoreLauncher / CoreSession abstraction in
    // Dmon.Runtime so a scripted FeedableReader can stand in for the process's stdout. This
    // abstraction is present and used in GatewayCreateE2ETests.
    // =========================================================================

    /// <summary>
    /// 6.1a — <see cref="NetworkConnectionEndpoint.DriveSessionHandshakeAsync"/> returns the
    /// session id allocated by the core and forwards the agent name in the
    /// <c>session.create</c> command written to core stdin.
    ///
    /// Seam: the static <c>internal</c> method is called directly with a scripted
    /// <see cref="FeedableReader"/> (simulating core stdout) and a <see cref="CapturingWriter"/>
    /// (capturing core stdin). This exercises the spec requirement "session created with the
    /// agent stored" without spawning a real OS process.
    /// </summary>
    [Theory]
    [InlineData("coding")]
    [InlineData("researcher")]
    [InlineData(null)]
    public async Task DriveSessionHandshake_ReturnsSessionId_AndForwardsAgentToStdin(string? agent)
    {
        // Arrange: scripted stdout that delivers the two correlated result lines.
        const string sessionId = "sess-6-1-a";
        FeedableReader stdout = new();
        CapturingWriter stdin = new();

        // Feed session.createResult correlated to the gateway's command id.
        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, agent));
        // Feed session.loadResult correlated to the gateway's load command id.
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        // Act.
        string returned = await NetworkConnectionEndpoint.DriveSessionHandshakeAsync(
            new StubCoreProcess(stdout, stdin), agent, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert: correct session id returned (spec: "session created with the agent stored").
        Assert.Equal(sessionId, returned);

        // Assert: the create command written to stdin contains the agent field correctly.
        // WireSerializerOptions.Default has WhenWritingNull, so a null agent is omitted entirely.
        // A non-null agent is serialised as "agent":"<name>".
        string writtenToCore = stdin.GetWritten();
        if (agent is not null)
        {
            Assert.Contains($"\"agent\":\"{agent}\"", writtenToCore);
        }
        else
        {
            // Null agent: the key is omitted entirely (WhenWritingNull).
            Assert.DoesNotContain("\"agent\"", writtenToCore);
            Assert.Contains("\"type\":\"session.create\"", writtenToCore);
        }
    }

    /// <summary>
    /// 6.1b — After <see cref="NetworkConnectionEndpoint.DriveSessionHandshakeAsync"/> completes,
    /// <see cref="SessionRegistry.TryRegister"/> accepts a new handler and
    /// <see cref="SessionRegistry.TryGet"/> returns that handler (so a subsequent attach succeeds).
    ///
    /// This asserts the spec requirement "the handler registered" and "the client can then attach".
    /// </summary>
    [Fact]
    public async Task CreateSucceeds_HandlerRegistered_AttachSucceeds()
    {
        // Arrange: perform the handshake at the DriveSessionHandshakeAsync seam.
        const string sessionId = "sess-6-1-b";
        FeedableReader stdout = new();
        CapturingWriter stdin = new();

        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, agent: "coding"));
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        string handshakeSessionId = await NetworkConnectionEndpoint.DriveSessionHandshakeAsync(
            new StubCoreProcess(stdout, stdin), agent: "coding", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(sessionId, handshakeSessionId);

        // Simulate what HandleCreateAsync does after the handshake: build handler, TryRegister.
        // Use the internal SessionHandler ctor that accepts TextReader/TextWriter — the same seam
        // used by all other SessionHandler tests — so no real CoreProcessManager is needed.
        FeedableReader liveStdout = new();
        CapturingWriter liveStdin = new();
        await using SessionHandler handler = new(handshakeSessionId, new SessionHandlerTestOptions { Stdout = liveStdout, Stdin = liveStdin });

        SessionRegistry registry = new();
        bool registered = registry.TryRegister(handshakeSessionId, handler, maxConcurrentHandlers: 10);

        // Assert: handler registered successfully (cap not reached).
        Assert.True(registered);

        // Assert: registry now contains the session (attach would succeed).
        SessionHandler? found = registry.TryGet(handshakeSessionId);
        Assert.NotNull(found);
        Assert.Equal(handshakeSessionId, found.SessionId);
    }

    /// <summary>
    /// 6.1c — The <c>created {sessionId}</c> control frame serialises correctly and carries
    /// the session id returned from the handshake.
    ///
    /// This asserts the spec requirement "created {sessionId} returned to the client".
    /// </summary>
    [Fact]
    public void CreatedFrame_SerializesSessionId_CorrectWireShape()
    {
        const string sessionId = "sess-wire-shape";
        string json = ControlFrameSerializer.Serialize(new CreatedFrame { SessionId = sessionId });

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("created", root.GetProperty("gw").GetString());
        Assert.Equal(sessionId, root.GetProperty("sessionId").GetString());
    }

    // =========================================================================
    // 6.2 — Create with unknown agent / null agent bypass
    // =========================================================================

    /// <summary>
    /// 6.2a — Requesting an unknown agent name: gateway replies with
    /// <c>createRejected {code="unknown_agent"}</c>, no handler is registered, no core spawned.
    ///
    /// Seam: <see cref="NetworkConnectionEndpoint.HandleCreateAsync"/> is called directly (internal)
    /// with a <see cref="CapturingWebSocket"/>. The <c>_coreLauncher</c> field is <c>null!</c>
    /// in the test ctor — if it were called it would NullReferenceException immediately,
    /// proving the rejection path exits before spawn.
    ///
    /// "Unknown" is detected because the agent .cs file does not exist under the workspace root.
    /// A temporary directory is used so no real workspace is created.
    /// </summary>
    [Fact]
    public async Task HandleCreate_UnknownAgent_ReturnsCreateRejected_NoCoreSpawned()
    {
        const string requestedAgent = "not-a-real-agent";
        SessionRegistry registry = new();

        // Use a temp directory that exists (so the path check is filesystem-backed)
        // but contains no .dmon/agents/ subtree → the agent .cs will not be found.
        string workspace = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspace);
        try
        {
            NetworkConnectionEndpoint endpoint = MakeEndpointWithWorkspace(registry, workspace);

            CapturingWebSocket socket = new();
            string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Agent = requestedAgent });

            // Act.
            await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

            // Assert: exactly one createRejected frame sent to the client.
            string sentJson = Assert.Single(socket.SentFrames);
            using JsonDocument doc = JsonDocument.Parse(sentJson);
            Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
            Assert.Equal("unknown_agent", doc.RootElement.GetProperty("code").GetString());

            // The message must be actionable (non-empty).
            string message = doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(message));

            // Assert: registry count unchanged (no handler leaked on rejection).
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// 6.2a-traversal — Path-traversal payloads in the agent name are rejected with
    /// <c>createRejected {code="unknown_agent"}</c> before any core is spawned.
    ///
    /// The containment guard in <see cref="NetworkConnectionEndpoint.HandleCreateAsync"/>
    /// uses <c>StartsWith(workspaceRoot + DirectorySeparatorChar, …)</c> after
    /// <see cref="Path.GetFullPath"/> to ensure the resolved agent path cannot escape the
    /// workspace root via <c>../</c> segments or an absolute path. This test verifies that
    /// each traversal variant is caught and results in the same rejection as a plain
    /// unknown-agent name — no core is spawned, registry count stays at zero.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    public async Task HandleCreate_TraversalAgent_ReturnsCreateRejected_NoCoreSpawned(string traversalAgent)
    {
        SessionRegistry registry = new();

        string workspace = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspace);
        try
        {
            NetworkConnectionEndpoint endpoint = MakeEndpointWithWorkspace(registry, workspace);

            CapturingWebSocket socket = new();
            string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Agent = traversalAgent });

            // Act.
            await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

            // Assert: exactly one createRejected frame sent to the client.
            string sentJson = Assert.Single(socket.SentFrames);
            using JsonDocument doc = JsonDocument.Parse(sentJson);
            Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
            Assert.Equal("unknown_agent", doc.RootElement.GetProperty("code").GetString());

            // The message must be actionable (non-empty).
            string message = doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(message));

            // Assert: no handler was registered — traversal payload never reaches spawn.
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// 6.2b — A null/absent agent bypasses validation entirely: no createRejected is sent.
    ///
    /// The test ctor has _coreLauncher = null!, so HandleCreateAsync will fail at the spawn step
    /// (NRE caught by the inner exception handler, which closes the socket with 4500). What matters
    /// for this spec assertion is that no createRejected frame was sent — confirming validation
    /// was bypassed for a null agent.
    /// </summary>
    [Fact]
    public async Task HandleCreate_NullAgent_ValidationSkipped_NoCreateRejected()
    {
        SessionRegistry registry = new();
        NetworkConnectionEndpoint endpoint = new(
            registry,
            new NetworkConnectionEndpoint.TestOptions(),
            NullLogger<NetworkConnectionEndpoint>.Instance);

        CapturingWebSocket socket = new();
        // Create frame with no agent field (agent = null).
        string createFrameNoAgent = ControlFrameSerializer.Serialize(new CreateFrame { Agent = null });

        // Act: HandleCreateAsync completes normally — the null _coreLauncher NRE is caught by the
        // inner exception handler which closes the socket with 4500. The method does NOT rethrow.
        await endpoint.HandleCreateAsync(socket, createFrameNoAgent, CancellationToken.None);

        // Assert: no createRejected frame was sent — validation was not triggered.
        Assert.DoesNotContain(socket.SentFrames, f => f.Contains("createRejected"));

        // Registry unchanged — the spawn failure leaves no handler registered.
        Assert.Equal(0, registry.Count);
    }

    // =========================================================================
    // 6.3 — Create at cap / reattach exempt from cap
    // =========================================================================

    /// <summary>
    /// 6.3a — When <see cref="SessionRegistry.TryRegister"/> is called with the cap already
    /// reached, it returns <c>false</c> and the registry count stays at the cap.
    ///
    /// Seam: exercises <see cref="SessionRegistry.TryRegister"/> directly to assert the cap
    /// enforcement that <c>HandleCreateAsync</c> relies on. The spec requires "no registry entry"
    /// after a cap-reached rejection; this test confirms the gate fires correctly.
    /// </summary>
    [Fact]
    public async Task TryRegister_AtCap_ReturnsFalse_RegistryCountUnchanged()
    {
        const int cap = 2;
        SessionRegistry registry = new();

        // Fill to the cap.
        await using SessionHandler h1 = new("s-cap-1", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });
        await using SessionHandler h2 = new("s-cap-2", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });

        Assert.True(registry.TryRegister("s-cap-1", h1, cap));
        Assert.True(registry.TryRegister("s-cap-2", h2, cap));
        Assert.Equal(cap, registry.Count);

        // Attempt to register a third session — must fail.
        await using SessionHandler h3 = new("s-cap-3", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });
        bool registered = registry.TryRegister("s-cap-3", h3, cap);

        Assert.False(registered);
        Assert.Equal(cap, registry.Count);       // count unchanged
        Assert.Null(registry.TryGet("s-cap-3")); // no handler leaked
    }

    /// <summary>
    /// 6.3b — The <c>createRejected {code="cap_reached"}</c> frame carries an actionable message
    /// and has the correct wire shape.
    /// </summary>
    [Fact]
    public void CapReachedFrame_SerializesCorrectly()
    {
        CreateRejectedFrame frame = new()
        {
            Code = "cap_reached",
            Message = "The gateway has reached its concurrent-session limit (2). " +
                      "Disconnect an existing session and retry.",
        };

        string json = ControlFrameSerializer.Serialize(frame);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("cap_reached", doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            doc.RootElement.GetProperty("message").GetString()));
    }

    /// <summary>
    /// 6.3c — Reattach to an already-registered session is exempt from the cap:
    /// <see cref="SessionRegistry.TryRegister"/> succeeds even when the cap is reached
    /// if the session id is already present.
    ///
    /// Spec scenario: "Reattach is exempt from the cap — the attach succeeds, since reattach
    /// does not allocate a new handler."
    /// </summary>
    [Fact]
    public async Task TryRegister_ReattachExistingSession_ExemptFromCap()
    {
        const int cap = 1;
        SessionRegistry registry = new();

        await using SessionHandler existing = new("s-existing", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });
        bool firstReg = registry.TryRegister("s-existing", existing, cap);
        Assert.True(firstReg);
        Assert.Equal(1, registry.Count); // at cap

        // Re-registering the same session id must succeed even at cap.
        await using SessionHandler replacement = new("s-existing", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });
        bool reReg = registry.TryRegister("s-existing", replacement, cap);

        Assert.True(reReg);              // exempt
        Assert.Equal(1, registry.Count); // count stays at 1 (not 2)

        // The replacement handler is now retrievable.
        Assert.Equal(replacement, registry.TryGet("s-existing"));
    }

    /// <summary>
    /// 6.3d — When a new session's <c>TryRegister</c> is rejected at cap, the handler that
    /// was just created can be disposed cleanly — confirming the "tear down the just-spawned core"
    /// path leaves no orphan in the registry.
    ///
    /// This mirrors what <c>HandleCreateAsync</c> does: call <c>TryRegister</c>; on failure,
    /// call <c>DisposeAsync</c> on the handler; the registry contains no entry for the new session.
    /// </summary>
    [Fact]
    public async Task CapReached_NewHandlerDisposedCleanly_NoOrphanInRegistry()
    {
        const int cap = 1;
        SessionRegistry registry = new();

        await using SessionHandler existing = new("s-present", new SessionHandlerTestOptions { Stdout = new NeverReadingReader(), Stdin = new StringWriter() });
        registry.TryRegister("s-present", existing, cap);

        // Simulate HandleCreateAsync: create handler, try register (fails at cap), dispose.
        FeedableReader orphanStdout = new();
        StringWriter orphanStdin = new();
        SessionHandler orphan = new("s-orphan", new SessionHandlerTestOptions { Stdout = orphanStdout, Stdin = orphanStdin });

        bool registered = registry.TryRegister("s-orphan", orphan, cap);
        Assert.False(registered);

        // HandleCreateAsync calls DisposeAsync on the handler it just built.
        await orphan.DisposeAsync();

        // No handler registered; registry count unchanged.
        Assert.Equal(1, registry.Count);
        Assert.Null(registry.TryGet("s-orphan"));
    }

    // =========================================================================
    // Helpers and test doubles
    // =========================================================================

    /// <summary>
    /// Builds a <see cref="NetworkConnectionEndpoint"/> with a supplied workspace root and
    /// no core launcher — valid only for tests that assert rejection paths that exit before spawn.
    /// </summary>
    private static NetworkConnectionEndpoint MakeEndpointWithWorkspace(
        SessionRegistry registry,
        string workspaceRoot)
    {
        return new NetworkConnectionEndpoint(
            registry,
            new NetworkConnectionEndpoint.TestOptions
            {
                WorkspaceRoot = workspaceRoot,
            },
            NullLogger<NetworkConnectionEndpoint>.Instance);
    }

    /// <summary>
    /// Builds the JSON line for a <c>session.createResult</c> event correlated to
    /// <paramref name="commandId"/> with the given <paramref name="sessionId"/>.
    /// </summary>
    private static string MakeCreateResult(string commandId, string sessionId, string? agent) =>
        JsonSerializer.Serialize(new
        {
            type = "session.createResult",
            id = commandId,
            session = new
            {
                id = sessionId,
                created = DateTimeOffset.UtcNow,
                modified = DateTimeOffset.UtcNow,
                agent,
            },
        });

    /// <summary>
    /// Builds the JSON line for a <c>session.loadResult</c> event correlated to
    /// <paramref name="commandId"/>.
    /// </summary>
    private static string MakeLoadResult(string commandId, string sessionId) =>
        JsonSerializer.Serialize(new
        {
            type = "session.loadResult",
            id = commandId,
            session = new
            {
                id = sessionId,
                created = DateTimeOffset.UtcNow,
                modified = DateTimeOffset.UtcNow,
            },
        });

    /// <summary>
    /// A WebSocket that captures all <see cref="SendAsync"/> calls as UTF-8 text frames
    /// and records all <see cref="CloseAsync"/> calls. Used to assert the wire frames sent
    /// by <see cref="NetworkConnectionEndpoint.HandleCreateAsync"/>.
    /// </summary>
    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly List<string> _sentFrames = [];
        private readonly List<WebSocketCloseStatus> _closeCalls = [];
        private WebSocketState _state = WebSocketState.Open;

        public IReadOnlyList<string> SentFrames => _sentFrames;
        public IReadOnlyList<WebSocketCloseStatus> CloseCalls => _closeCalls;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            // HandleCreateAsync does not call ReceiveAsync — it operates on the pre-parsed
            // rawCreateFrame string. Return Close immediately as a safe fallback.
            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
                _sentFrames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeCalls.Add(closeStatus);
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }

    /// <summary>A stdout reader that blocks until a line is fed or the channel is closed.</summary>
    private sealed class FeedableReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Feed(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() =>
            ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>Captures text written to core stdin.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _sb.Append(buffer.Span);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public string GetWritten() => _sb.ToString();
    }

    /// <summary>A stdout reader that blocks indefinitely without propagating cancellation.</summary>
    private sealed class NeverReadingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>
    /// Minimal <see cref="ICoreProcess"/> adapter that wraps a caller-supplied
    /// <see cref="TextReader"/> and <see cref="TextWriter"/>. Lifecycle methods are no-ops.
    /// Used to pass scripted reader/writer pairs to <see cref="NetworkConnectionEndpoint.DriveSessionHandshakeAsync"/>.
    /// </summary>
    private sealed class StubCoreProcess : ICoreProcess
    {
        public StubCoreProcess(TextReader standardOutput, TextWriter standardInput)
        {
            StandardOutput = standardOutput;
            StandardInput = standardInput;
        }

        public TextReader StandardOutput { get; }
        public TextWriter StandardInput { get; }
        public bool IsRunning => true;

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RestartAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
