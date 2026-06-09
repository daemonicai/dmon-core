using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dmon.Abstractions.Profiles;
using Dmon.Core.Config;
using Dmon.Core.Profiles;
using Dmon.Gateway.Protocol;
using Dmon.Gateway.Sessions;
using Dmon.Protocol.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 6: gateway create-flow tests.
///
/// 6.1 — Create with a known profile: handshake returns the session id, profile is forwarded
///        to core stdin, handler is registered, and <c>created {sessionId}</c> is sent to the client.
///        A subsequent attach to the registered session succeeds.
///
/// 6.2 — Create with an unknown or misconfigured profile: a typed <c>createRejected</c> is returned,
///        the registry count is unchanged, and no core is spawned
///        (<see cref="GatewayConnectionEndpoint.HandleCreateAsync"/> returns before calling
///        <see cref="CoreLauncher"/>). Both the <c>unknown_profile</c> and <c>invalid_profile</c>
///        error codes are covered.
///
/// 6.3 — Create at cap: <c>createRejected {code="cap_reached"}</c> is returned and the registry
///        count remains at the cap. Reattach to an already-registered session is exempt from the cap.
/// </summary>
public sealed class GatewayCreateFlowTests
{
    // =========================================================================
    // 6.1 — Create with a known profile
    //
    // Coverage boundary (intentional — not a gap):
    //
    // The 6.1 success path is verified through three decomposed seam tests:
    //   6.1a — DriveSessionHandshakeAsync (handshake result consumption + profile forwarding).
    //   6.1b — SessionRegistry.TryRegister + TryGet (handler registration / attach lookup).
    //   6.1c — CreatedFrame wire shape (the frame returned to the client).
    //
    // What is NOT covered by automation is the in-HandleCreateAsync ordering between these parts —
    // specifically the ADR-014-critical sequencing that DriveSessionHandshakeAsync consumes BOTH
    // handshake result lines (session.createResult + session.loadResult) BEFORE new SessionHandler(...)
    // starts the seq-assigning stdout pump. A full HandleCreateAsync end-to-end test would need to
    // launch a real (or fake) OS process via an ICoreLauncher / CoreSession abstraction in
    // Dmon.Runtime so a scripted FeedableReader can stand in for the process's stdout. Introducing
    // that abstraction is a cross-project change (Dmon.Runtime + Dmon.Gateway public surface) that
    // is deliberately out of scope for this change (per-session-profile-selection). The composition
    // ordering is visible in GatewayConnectionEndpoint.HandleCreateAsync at the DriveSessionHandshakeAsync
    // call site and is covered by code review.
    // =========================================================================

    /// <summary>
    /// 6.1a — <see cref="GatewayConnectionEndpoint.DriveSessionHandshakeAsync"/> returns the
    /// session id allocated by the core and forwards the profile name in the
    /// <c>session.create</c> command written to core stdin.
    ///
    /// Seam: the static <c>internal</c> method is called directly with a scripted
    /// <see cref="FeedableReader"/> (simulating core stdout) and a <see cref="CapturingWriter"/>
    /// (capturing core stdin). This exercises the spec requirement "session created with the
    /// profile stored" without spawning a real OS process.
    /// </summary>
    [Theory]
    [InlineData("coding")]
    [InlineData("researcher")]
    [InlineData(null)]
    public async Task DriveSessionHandshake_ReturnsSessionId_AndForwardsProfileToStdin(string? profile)
    {
        // Arrange: scripted stdout that delivers the two correlated result lines.
        const string sessionId = "sess-6-1-a";
        FeedableReader stdout = new();
        CapturingWriter stdin = new();

        // Feed session.createResult correlated to the gateway's command id.
        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, profile));
        // Feed session.loadResult correlated to the gateway's load command id.
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        // Act.
        string returned = await GatewayConnectionEndpoint.DriveSessionHandshakeAsync(
            stdout, stdin, profile, CancellationToken.None);

        // Assert: correct session id returned (spec: "session created with the profile stored").
        Assert.Equal(sessionId, returned);

        // Assert: the create command written to stdin contains the profile field correctly.
        // JsonOptions has no DefaultIgnoreCondition = WhenWritingNull, so a null profile is
        // serialised as "profile":null (the key IS present). A non-null profile is serialised
        // as "profile":"<name>".
        string writtenToCore = stdin.GetWritten();
        if (profile is not null)
        {
            Assert.Contains($"\"profile\":\"{profile}\"", writtenToCore);
        }
        else
        {
            // Null profile: the key is emitted as "profile":null, not omitted.
            Assert.Contains("\"profile\":null", writtenToCore);
            Assert.Contains("\"type\":\"session.create\"", writtenToCore);
        }
    }

    /// <summary>
    /// 6.1b — After <see cref="GatewayConnectionEndpoint.DriveSessionHandshakeAsync"/> completes,
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

        stdout.Feed(MakeCreateResult("gw-session-create", sessionId, profile: "coding"));
        stdout.Feed(MakeLoadResult("gw-session-load", sessionId));

        string handshakeSessionId = await GatewayConnectionEndpoint.DriveSessionHandshakeAsync(
            stdout, stdin, profile: "coding", CancellationToken.None);

        Assert.Equal(sessionId, handshakeSessionId);

        // Simulate what HandleCreateAsync does after the handshake: build handler, TryRegister.
        // Use the internal SessionHandler ctor that accepts TextReader/TextWriter — the same seam
        // used by all other SessionHandler tests — so no real CoreProcessManager is needed.
        FeedableReader liveStdout = new();
        CapturingWriter liveStdin = new();
        await using SessionHandler handler = new(handshakeSessionId, liveStdout, liveStdin);

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
    // 6.2 — Create with unknown or misconfigured profile
    // =========================================================================

    /// <summary>
    /// 6.2a — Requesting an unknown profile name: gateway replies with
    /// <c>createRejected {code="unknown_profile"}</c>, no handler is registered, no core spawned.
    ///
    /// Seam: <see cref="GatewayConnectionEndpoint.HandleCreateAsync"/> is called directly (internal)
    /// with a <see cref="CapturingWebSocket"/> and a fake <see cref="IAgentProfileResolver"/> that
    /// throws. The <c>_coreLauncher</c> field is <c>null!</c> in the test ctor — if it were called
    /// it would NullReferenceException immediately, proving the rejection path exits before spawn.
    ///
    /// "Unknown" is detected because the name is not "coding" (the only built-in) and the
    /// non-existent config file paths contribute an empty effective set.
    /// </summary>
    [Fact]
    public async Task HandleCreate_UnknownProfile_ReturnsCreateRejected_NoCoreSpawned()
    {
        // "not-a-real-profile" is not "coding" and not in any config file (paths don't exist).
        // ContainsProfile returns false → code = "unknown_profile".
        const string requestedProfile = "not-a-real-profile";
        SessionRegistry registry = new();

        // Resolver throws with an actionable message; ContainsProfile is driven by the real
        // EffectiveProfileSetResolver with non-existent config paths (returns empty set).
        ThrowingProfileResolver resolver = new(message: $"Profile '{requestedProfile}' not found.");

        GatewayConnectionEndpoint endpoint = MakeEndpointWithResolver(registry, resolver);

        CapturingWebSocket socket = new();
        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = requestedProfile });

        // Act.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: exactly one createRejected frame sent to the client.
        string sentJson = Assert.Single(socket.SentFrames);
        using JsonDocument doc = JsonDocument.Parse(sentJson);
        Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("unknown_profile", doc.RootElement.GetProperty("code").GetString());

        // The message must be actionable (non-empty).
        string message = doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(message));

        // Assert: registry count unchanged (no handler leaked on rejection).
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// 6.2b — Requesting the built-in <c>coding</c> profile when its config is invalid:
    /// gateway replies with <c>createRejected {code="invalid_profile"}</c>, no handler registered.
    ///
    /// Covers the distinction added in Group 5: the name IS in the effective set (built-in "coding"
    /// is always present), so ContainsProfile returns true → code = "invalid_profile".
    /// </summary>
    [Fact]
    public async Task HandleCreate_InvalidProfile_ReturnsCreateRejected_InvalidProfileCode()
    {
        // "coding" is always in the effective set (built-in).
        // ContainsProfile("coding", ...) returns true → code = "invalid_profile".
        const string requestedProfile = "coding";
        SessionRegistry registry = new();

        ThrowingProfileResolver resolver = new(message: "Profile 'coding' has invalid config.");

        GatewayConnectionEndpoint endpoint = MakeEndpointWithResolver(registry, resolver);

        CapturingWebSocket socket = new();
        string createFrame = ControlFrameSerializer.Serialize(new CreateFrame { Profile = requestedProfile });

        // Act.
        await endpoint.HandleCreateAsync(socket, createFrame, CancellationToken.None);

        // Assert: createRejected with invalid_profile code.
        string sentJson = Assert.Single(socket.SentFrames);
        using JsonDocument doc = JsonDocument.Parse(sentJson);
        Assert.Equal("createRejected", doc.RootElement.GetProperty("gw").GetString());
        Assert.Equal("invalid_profile", doc.RootElement.GetProperty("code").GetString());

        // Assert: registry unchanged — no handler leaked.
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// 6.2c — A null/absent profile bypasses validation entirely: the resolver is never called
    /// and no createRejected is sent.
    ///
    /// Design D3: "A null/absent profile is valid and resolves to the configured default
    /// — do not reject it."
    ///
    /// The test ctor has _coreLauncher = null!, so HandleCreateAsync will fail at the spawn step
    /// (NRE caught by the inner exception handler, which closes the socket with 4500). What matters
    /// for this spec assertion is that no createRejected frame was sent and ResolveAsync was
    /// never invoked — both confirming validation was bypassed for a null profile.
    /// </summary>
    [Fact]
    public async Task HandleCreate_NullProfile_ValidationSkipped_NoCreateRejected()
    {
        SessionRegistry registry = new();
        TrackingProfileResolver resolver = new();

        GatewayConnectionEndpoint endpoint = MakeEndpointWithResolver(registry, resolver);

        CapturingWebSocket socket = new();
        // Create frame with no profile field (profile = null).
        string createFrameNoProfile = ControlFrameSerializer.Serialize(new CreateFrame { Profile = null });

        // Act: HandleCreateAsync completes normally — the null _coreLauncher NRE is caught by the
        // inner exception handler which closes the socket with 4500. The method does NOT rethrow.
        await endpoint.HandleCreateAsync(socket, createFrameNoProfile, CancellationToken.None);

        // Assert: no createRejected frame was sent — validation was not triggered.
        Assert.DoesNotContain(socket.SentFrames, f => f.Contains("createRejected"));

        // Assert: ResolveAsync was NOT called for a null profile (design D3).
        Assert.False(resolver.WasCalled,
            "ResolveAsync must not be called when profile is null (design D3).");

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
        await using SessionHandler h1 = new("s-cap-1", new NeverReadingReader(), new StringWriter());
        await using SessionHandler h2 = new("s-cap-2", new NeverReadingReader(), new StringWriter());

        Assert.True(registry.TryRegister("s-cap-1", h1, cap));
        Assert.True(registry.TryRegister("s-cap-2", h2, cap));
        Assert.Equal(cap, registry.Count);

        // Attempt to register a third session — must fail.
        await using SessionHandler h3 = new("s-cap-3", new NeverReadingReader(), new StringWriter());
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

        await using SessionHandler existing = new("s-existing", new NeverReadingReader(), new StringWriter());
        bool firstReg = registry.TryRegister("s-existing", existing, cap);
        Assert.True(firstReg);
        Assert.Equal(1, registry.Count); // at cap

        // Re-registering the same session id must succeed even at cap.
        await using SessionHandler replacement = new("s-existing", new NeverReadingReader(), new StringWriter());
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

        await using SessionHandler existing = new("s-present", new NeverReadingReader(), new StringWriter());
        registry.TryRegister("s-present", existing, cap);

        // Simulate HandleCreateAsync: create handler, try register (fails at cap), dispose.
        FeedableReader orphanStdout = new();
        StringWriter orphanStdin = new();
        SessionHandler orphan = new("s-orphan", orphanStdout, orphanStdin);

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
    /// Builds a <see cref="GatewayConnectionEndpoint"/> wired with a real
    /// <see cref="EffectiveProfileSetResolver"/> (default ctor reads from config files;
    /// non-existent paths return an empty set) and the supplied profile resolver.
    /// <c>_coreLauncher</c> is <c>null!</c> — only valid for tests that assert rejection
    /// paths that return before the spawn step.
    /// </summary>
    private static GatewayConnectionEndpoint MakeEndpointWithResolver(
        SessionRegistry registry,
        IAgentProfileResolver resolver)
    {
        // Non-existent paths → ProfilesConfigReader returns empty (optional: true).
        GatewayProfilePaths paths = new(
            UserConfigPath: "/dev/null/nonexistent-user.yaml",
            ProjectConfigPath: "/dev/null/nonexistent-project.yaml");

        return new GatewayConnectionEndpoint(
            registry,
            resolver,
            new EffectiveProfileSetResolver(),
            paths,
            new StaticOptionsMonitor(new GatewayOptions()),
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
    /// Fake <see cref="IAgentProfileResolver"/> that always throws
    /// <see cref="AgentProfileConfigException"/> with a fixed message. Tracks whether
    /// <see cref="ResolveAsync"/> was called so tests can assert it was skipped for null profiles.
    /// </summary>
    private sealed class ThrowingProfileResolver : IAgentProfileResolver
    {
        private readonly string _message;

        public ThrowingProfileResolver(string message)
        {
            _message = message;
        }

        public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken)
        {
            throw new AgentProfileConfigException(_message);
        }
    }

    /// <summary>
    /// Fake <see cref="IAgentProfileResolver"/> that tracks whether it was called,
    /// without throwing. Used to assert that null-profile validation is bypassed.
    /// </summary>
    private sealed class TrackingProfileResolver : IAgentProfileResolver
    {
        public bool WasCalled { get; private set; }

        public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new AgentProfile("coding", "", false, PermissionMode.Coding));
        }
    }

    /// <summary>
    /// A WebSocket that captures all <see cref="SendAsync"/> calls as UTF-8 text frames
    /// and records all <see cref="CloseAsync"/> calls. Used to assert the wire frames sent
    /// by <see cref="GatewayConnectionEndpoint.HandleCreateAsync"/>.
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
}
