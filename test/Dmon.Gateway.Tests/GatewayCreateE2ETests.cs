using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dmon.Abstractions.Profiles;
using Dmon.Core.Config;
using Dmon.Core.Profiles;
using Dmon.Gateway.Protocol;
using Dmon.Gateway.Sessions;
using Dmon.Protocol.Events;
using Dmon.Protocol.Gateway;
using Dmon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 2: in-process fake core end-to-end tests for
/// <see cref="GatewayConnectionEndpoint.HandleCreateAsync"/>.
///
/// 2.1 — <see cref="FakeCoreLauncher"/> / <see cref="FakeCoreProcess"/> doubles (defined below).
///
/// 2.2 — Happy path: scripted handshake → <c>created</c> frame, handler registered and attachable.
///
/// 2.3 — Seq-ordering: handshake results are excluded from the event stream; the lowest seq
///        assigned corresponds to the first post-handshake event, not a handshake result.
///
/// 2.4 — Failure paths: (a) timeout → <c>core_timeout</c> + core torn down;
///        (b) spawn/handshake exception → socket closed 4500 + core torn down;
///        (c) cap reached → <c>cap_reached</c> + spawned core torn down.
/// </summary>
public sealed class GatewayCreateE2ETests
{
    // =========================================================================
    // 2.2 — Happy path
    // =========================================================================

    /// <summary>
    /// 2.2 — Drives the real <see cref="GatewayConnectionEndpoint.HandleCreateAsync"/> via a
    /// scripted <see cref="FakeCoreLauncher"/>. Asserts that:
    /// <list type="bullet">
    ///   <item>The gateway wrote a <c>session.create</c> then a path-less <c>session.load</c>
    ///     command to the fake stdin (in that order).</item>
    ///   <item>The client WebSocket received a <c>created</c> frame carrying the expected session id.</item>
    ///   <item>The handler is registered in the registry (a subsequent <see cref="SessionRegistry.TryGet"/>
    ///     succeeds).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task HandleCreate_HappyPath_CreatedFrameSent_HandlerRegistered()
    {
        const string sessionId = "e2e-happy-sess";

        FeedableReader stdout = new();
        CapturingWriter stdin = new();
        FakeCoreProcess process = new(stdout, stdin);
        FakeCoreLauncher launcher = new(process);

        // Script the two correlated handshake results.
        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, profile: null));
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        SessionRegistry registry = new();
        CapturingWebSocket socket = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        // Act.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: created frame was sent to the client.
        Assert.Single(socket.SentFrames);
        using JsonDocument doc = JsonDocument.Parse(socket.SentFrames[0]);
        Assert.Equal("created", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("sessionId").GetString());

        // Assert: handler registered in the registry.
        SessionHandler? handler = registry.TryGet(sessionId);
        Assert.NotNull(handler);
        Assert.Equal(sessionId, handler.SessionId);

        // Assert: session.create and session.load were written to core stdin, in that order.
        string writtenToCore = stdin.GetWritten();
        int createPos = writtenToCore.IndexOf("\"session.create\"", StringComparison.Ordinal);
        int loadPos = writtenToCore.IndexOf("\"session.load\"", StringComparison.Ordinal);
        Assert.True(createPos >= 0, "session.create command not found in stdin");
        Assert.True(loadPos >= 0, "session.load command not found in stdin");
        Assert.True(createPos < loadPos, "session.create must precede session.load in stdin");

        await handler.DisposeAsync();
    }

    // =========================================================================
    // 2.3 — Seq-ordering: handshake results are excluded from the event stream
    // =========================================================================

    /// <summary>
    /// 2.3 — After a successful create, the first seq-assigned event is the post-handshake event,
    /// not a handshake result. Spec requirement: "Create-handshake results are excluded from
    /// the event stream."
    ///
    /// Verifies that neither <c>session.createResult</c> nor <c>session.loadResult</c> appear
    /// in any frame delivered to an attached client, and that the lowest seq in the delivered
    /// stream corresponds to a distinct post-handshake event.
    /// </summary>
    [Fact]
    public async Task HandleCreate_HandshakeResultsExcludedFromSeqStream()
    {
        const string sessionId = "e2e-seq-sess";
        const string postHandshakeEventLine = """{"type":"agentStart"}""";

        FeedableReader stdout = new();
        CapturingWriter stdin = new();
        FakeCoreProcess process = new(stdout, stdin);
        FakeCoreLauncher launcher = new(process);

        // Script the two handshake results, then feed a distinct post-handshake event.
        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, profile: null));
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        // The post-handshake event is fed BEFORE the handler starts to avoid a race
        // where the pump tries to read and blocks before the line is fed.
        stdout.Feed(postHandshakeEventLine);

        SessionRegistry registry = new();
        CapturingWebSocket socket = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        // Act: drive the create handshake.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // The handler is now constructed and its pump is running.
        SessionHandler? handler = registry.TryGet(sessionId);
        Assert.NotNull(handler);

        // Attach a recording connection.
        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);

        // Wait for the post-handshake event to be delivered (seq 1).
        IReadOnlyList<string> delivered = await conn.WaitForCountAsync(1);

        // Assert: no delivered frame contains a session.createResult or session.loadResult.
        Assert.DoesNotContain(delivered, f => f.Contains("session.createResult"));
        Assert.DoesNotContain(delivered, f => f.Contains("session.loadResult"));

        // Assert: the delivered frame contains the post-handshake event content.
        Assert.Contains(delivered, f => f.Contains("agentStart"));

        // Assert: the lowest seq in the stream is 1 (the post-handshake event gets seq 1,
        // not seq 3 — i.e. the handshake results were NOT pumped through and did not consume seqs).
        Assert.Equal(1, handler.HeadSeq);

        await handler.DisposeAsync();
    }

    // =========================================================================
    // 2.4a — Handshake timeout → CreateRejectedFrame{code:"core_timeout"} + core torn down
    // =========================================================================

    /// <summary>
    /// 2.4a — When the fake core never feeds any handshake result lines and
    /// <c>CreateHandshakeTimeoutSeconds</c> is set to a small value (1s), the timeout fires,
    /// the gateway sends <c>createRejected {code:"core_timeout"}</c>, and
    /// <see cref="ICoreProcess.StopAsync"/> + <see cref="IDisposable.Dispose"/> are called
    /// on the fake core (no orphan).
    ///
    /// The outer <see cref="CancellationToken"/> is <see cref="CancellationToken.None"/> so
    /// the catch clause <c>when (!cancellationToken.IsCancellationRequested)</c> fires — the
    /// timeout path, not the client-abort path.
    /// </summary>
    [Fact]
    public async Task HandleCreate_HandshakeTimeout_CoreTornDown_TimeoutRejectionSent()
    {
        FeedableReader stdout = new();
        CapturingWriter stdin = new();
        FakeCoreProcess process = new(stdout, stdin);
        FakeCoreLauncher launcher = new(process);

        // No lines are fed to stdout — the handshake will stall until the timeout fires.

        SessionRegistry registry = new();
        CapturingWebSocket socket = new();

        // Set a very short timeout so the test is fast.
        GatewayOptions opts = new() { CreateHandshakeTimeoutSeconds = 1, MaxConcurrentHandlers = 10 };
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher, opts);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        // Act. CancellationToken.None ensures the outer token is not IsCancellationRequested,
        // so the timeout branch (not the client-abort branch) is taken.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: createRejected with code=core_timeout was sent to the client.
        Assert.Single(socket.SentFrames);
        using JsonDocument doc = JsonDocument.Parse(socket.SentFrames[0]);
        Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("core_timeout", doc.RootElement.GetProperty("code").GetString());

        // Assert: no handler was registered.
        Assert.Equal(0, registry.Count);

        // Assert: the fake core was stopped and disposed (no orphan).
        Assert.True(process.StopAsyncCalled, "StopAsync must be called on timeout");
        Assert.True(process.DisposeCalled, "Dispose must be called on timeout");
    }

    // =========================================================================
    // 2.4b — Spawn/handshake exception → socket closed 4500 + core torn down
    // =========================================================================

    /// <summary>
    /// 2.4b — When the launcher throws during <c>StartProtocolCompatibleCoreAsync</c>, the
    /// gateway closes the WebSocket with status 4500 ("session create failed").
    /// No core was spawned so teardown assertions do not apply.
    /// </summary>
    [Fact]
    public async Task HandleCreate_LauncherThrows_SocketClosedWith4500()
    {
        ThrowingCoreLauncher launcher = new(new InvalidOperationException("core spawn failed"));

        SessionRegistry registry = new();
        CapturingWebSocket socket = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: socket closed with status 4500.
        Assert.Single(socket.CloseCalls);
        Assert.Equal((WebSocketCloseStatus)4500, socket.CloseCalls[0]);

        // Assert: no handler registered.
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// 2.4b (variant) — When the core feeds a <c>commandError</c> correlated to the
    /// <c>gw-session-create</c> command, <see cref="GatewayConnectionEndpoint.DriveSessionHandshakeAsync"/>
    /// throws <see cref="InvalidOperationException"/>, and the gateway closes the socket with 4500
    /// and tears down the core (StopAsync + Dispose called).
    /// </summary>
    [Fact]
    public async Task HandleCreate_HandshakeCommandError_SocketClosedWith4500_CoreTornDown()
    {
        const string errorLine = """{"type":"commandError","id":"gw-session-create","command":"session.create","code":"noActiveSession","message":"handshake failed"}""";

        FeedableReader stdout = new();
        CapturingWriter stdin = new();
        FakeCoreProcess process = new(stdout, stdin);
        FakeCoreLauncher launcher = new(process);

        // Feed a commandError so DriveSessionHandshakeAsync throws.
        stdout.Feed(errorLine);

        SessionRegistry registry = new();
        CapturingWebSocket socket = new();
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: socket closed with status 4500.
        Assert.Single(socket.CloseCalls);
        Assert.Equal((WebSocketCloseStatus)4500, socket.CloseCalls[0]);

        // Assert: no handler registered.
        Assert.Equal(0, registry.Count);

        // Assert: core was torn down (no orphan).
        Assert.True(process.StopAsyncCalled, "StopAsync must be called after handshake exception");
        Assert.True(process.DisposeCalled, "Dispose must be called after handshake exception");
    }

    // =========================================================================
    // 2.4c — Cap reached → CreateRejectedFrame{code:"cap_reached"} + core torn down
    // =========================================================================

    /// <summary>
    /// 2.4c — When <c>MaxConcurrentHandlers</c> is 1 and the registry already holds one handler,
    /// driving a valid create succeeds through the handshake but <see cref="SessionRegistry.TryRegister"/>
    /// returns <c>false</c>. The gateway sends <c>createRejected {code:"cap_reached"}</c>, the
    /// new handler is NOT registered, and the freshly-spawned fake core is torn down (no orphan).
    /// </summary>
    [Fact]
    public async Task HandleCreate_CapReached_CapRejectionSent_SpawnedCoreTornDown()
    {
        const string existingSessionId = "e2e-existing";
        const string newSessionId = "e2e-new";

        // Pre-fill the registry to the cap (MaxConcurrentHandlers = 1).
        SessionRegistry registry = new();
        FeedableReader existingStdout = new();
        await using SessionHandler existingHandler = new(existingSessionId, existingStdout, new StringWriter());
        bool preRegistered = registry.TryRegister(existingSessionId, existingHandler, maxConcurrentHandlers: 1);
        Assert.True(preRegistered, "pre-registration must succeed");
        Assert.Equal(1, registry.Count);

        // Script the handshake for the NEW session (will succeed through DriveSessionHandshakeAsync).
        FeedableReader newStdout = new();
        CapturingWriter newStdin = new();
        FakeCoreProcess newProcess = new(newStdout, newStdin);
        FakeCoreLauncher launcher = new(newProcess);

        newStdout.Feed(MakeCreateResult("gw-session-create", newSessionId, profile: null));
        newStdout.Feed(MakeLoadResult("gw-session-load", newSessionId));

        CapturingWebSocket socket = new();
        GatewayOptions opts = new() { MaxConcurrentHandlers = 1, CreateHandshakeTimeoutSeconds = 30 };
        GatewayConnectionEndpoint endpoint = MakeEndpoint(registry, launcher, opts);

        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        // Act.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: createRejected with code=cap_reached was sent to the client.
        Assert.Single(socket.SentFrames);
        using JsonDocument doc = JsonDocument.Parse(socket.SentFrames[0]);
        Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("cap_reached", doc.RootElement.GetProperty("code").GetString());

        // Assert: only the original handler remains in the registry; no new entry.
        Assert.Equal(1, registry.Count);
        Assert.Null(registry.TryGet(newSessionId));

        // Assert: the spawned core was torn down (StopAsync + Dispose).
        Assert.True(newProcess.StopAsyncCalled, "StopAsync must be called when cap is reached");
        Assert.True(newProcess.DisposeCalled, "Dispose must be called when cap is reached");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Builds a <see cref="GatewayConnectionEndpoint"/> wired with the supplied
    /// <paramref name="launcher"/> and a pass-through profile resolver (accepts any profile).
    /// </summary>
    private static GatewayConnectionEndpoint MakeEndpoint(
        SessionRegistry registry,
        ICoreLauncher launcher,
        GatewayOptions? options = null)
    {
        GatewayOptions opts = options ?? new GatewayOptions();

        // Non-existent paths → EffectiveProfileSetResolver returns empty (files optional).
        GatewayProfilePaths paths = new(
            UserConfigPath: "/dev/null/nonexistent-user.yaml",
            ProjectConfigPath: "/dev/null/nonexistent-project.yaml");

        return new GatewayConnectionEndpoint(
            registry,
            launcher,
            new PassthroughProfileResolver(),
            new EffectiveProfileSetResolver(),
            paths,
            new StaticOptionsMonitor(opts),
            TimeProvider.System,
            NullLogger<GatewayConnectionEndpoint>.Instance);
    }

    /// <summary>
    /// Builds the JSON line for a <c>session.createResult</c> event correlated to
    /// <paramref name="commandId"/> with the given <paramref name="sessionId"/>.
    /// </summary>
    private static string MakeCreateResult(string commandId, string sessionId, string? profile) =>
        JsonSerializer.Serialize(new
        {
            type = "session.createResult",
            id = commandId,
            session = new
            {
                id = sessionId,
                created = DateTimeOffset.UtcNow,
                modified = DateTimeOffset.UtcNow,
                profile,
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

    // =========================================================================
    // Test doubles (tasks 2.1)
    // =========================================================================

    /// <summary>
    /// In-process fake for <see cref="ICoreProcess"/>. Backed by a caller-supplied
    /// <see cref="FeedableReader"/> (stdout) and <see cref="CapturingWriter"/> (stdin).
    /// Records all lifecycle method invocations so tests can assert teardown behaviour.
    /// </summary>
    internal sealed class FakeCoreProcess : ICoreProcess
    {
        private bool _isRunning = true;

        public FakeCoreProcess(TextReader standardOutput, TextWriter standardInput)
        {
            StandardOutput = standardOutput;
            StandardInput = standardInput;
        }

        public TextReader StandardOutput { get; }
        public TextWriter StandardInput { get; }
        public bool IsRunning => _isRunning;

        // Lifecycle recording.
        public bool StartAsyncCalled { get; private set; }
        public bool StopAsyncCalled { get; private set; }
        public bool RestartAsyncCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public Task StartAsync()
        {
            StartAsyncCalled = true;
            _isRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopAsyncCalled = true;
            _isRunning = false;
            return Task.CompletedTask;
        }

        public Task RestartAsync()
        {
            RestartAsyncCalled = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            _isRunning = false;
        }
    }

    /// <summary>
    /// In-process fake for <see cref="ICoreLauncher"/>. Returns a <see cref="CoreSession"/>
    /// wrapping the supplied <see cref="FakeCoreProcess"/> so tests can script stdout and
    /// inspect stdin without spawning an OS process.
    /// </summary>
    internal sealed class FakeCoreLauncher : ICoreLauncher
    {
        private readonly ICoreProcess _process;

        public FakeCoreLauncher(ICoreProcess process)
        {
            _process = process;
        }

        public Task<CoreSession> StartProtocolCompatibleCoreAsync(
            string? corePathOverride = null,
            string? workingDirectory = null,
            Action<string>? onStderrLine = null,
            CancellationToken cancellationToken = default)
        {
            AgentReadyEvent agentReady = new()
            {
                ProtocolVersion = "1.0",
                CoreVersion = "0.0.0-test",
            };
            return Task.FromResult(new CoreSession(_process, agentReady));
        }
    }

    /// <summary>
    /// A launcher that always throws a fixed exception from
    /// <see cref="StartProtocolCompatibleCoreAsync"/>, simulating a spawn failure.
    /// </summary>
    private sealed class ThrowingCoreLauncher : ICoreLauncher
    {
        private readonly Exception _exception;

        public ThrowingCoreLauncher(Exception exception)
        {
            _exception = exception;
        }

        public Task<CoreSession> StartProtocolCompatibleCoreAsync(
            string? corePathOverride = null,
            string? workingDirectory = null,
            Action<string>? onStderrLine = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CoreSession>(_exception);
    }

    /// <summary>
    /// Profile resolver that accepts any profile name, returning a minimal valid
    /// <see cref="AgentProfile"/>. Used to bypass profile validation for tests that
    /// exercise the post-validation path inside <see cref="GatewayConnectionEndpoint.HandleCreateAsync"/>.
    /// </summary>
    private sealed class PassthroughProfileResolver : IAgentProfileResolver
    {
        public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProfile(
                requestedProfile ?? "coding",
                "",
                false,
                PermissionMode.Coding));
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> backed by a static value.</summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<GatewayOptions>
    {
        private readonly GatewayOptions _value;
        public StaticOptionsMonitor(GatewayOptions value) => _value = value;
        public GatewayOptions CurrentValue => _value;
        public GatewayOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    /// <summary>
    /// Records frames in delivery order and lets a test await a target count.
    /// </summary>
    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _frames.Add(frame);
                _signal.TrySetResult();
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return ValueTask.CompletedTask;
        }

        public void Abort()
        {
        }

        public async Task<IReadOnlyList<string>> WaitForCountAsync(int count)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (_frames.Count >= count)
                        return [.. _frames];
                    waitTask = _signal.Task;
                }

                try
                {
                    await waitTask.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        throw new TimeoutException(
                            $"Expected {count} frames, saw {_frames.Count}: [{string.Join(", ", _frames)}]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A WebSocket that captures all <see cref="SendAsync"/> calls as UTF-8 text frames
    /// and records all <see cref="CloseAsync"/> calls. Mirrors the double in
    /// <see cref="GatewayCreateFlowTests"/>.
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

    /// <summary>A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed.</summary>
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
}
