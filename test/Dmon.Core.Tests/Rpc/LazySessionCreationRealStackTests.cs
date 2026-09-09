using Dmon.Core.Session;
using Dmon.Core.Rpc;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Proves the lazy-session-creation fix end-to-end against a real <see cref="SessionHandler"/>
/// over a real <see cref="SessionStore"/> rooted at an isolated temp directory — no
/// <c>StubSessionHandler</c>/<c>ActiveSessionHandler</c>/<c>SpySessionStore</c> fakes, because
/// those mint a <see cref="SessionMeta"/> in memory without ever touching disk and therefore
/// cannot prove persistence, directory absence, or fork/load parity.
/// </summary>
public sealed class LazySessionCreationRealStackTests
{
    // ── real-stack wiring ───────────────────────────────────────────────────

    private static AIFunction MakeStubTool(string name, string result)
        => AIFunctionFactory.Create((string input) => result, name, $"Stub tool {name}");

    private static ISessionStore BuildRealStore(string root)
    {
        FakeResolver resolver = new(root);
        IConfiguration configuration = new ConfigurationBuilder().Build();
        IAttachmentStore attachmentStore = new AttachmentStore(resolver, configuration);
        return new SessionStore(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, configuration);
    }

    private sealed class FakeResolver : ISessionDirectoryResolver
    {
        private readonly string _root;
        public FakeResolver(string root) => _root = root;
        public string Resolve(string workingDirectory) => _root;
    }

    /// <summary>
    /// An isolated, not-yet-created temp directory. Deliberately left uncreated so a test can
    /// assert its non-existence proves nothing ever wrote to it; cleaned up best-effort on
    /// dispose so tests never litter the host temp dir (and never the repo's own .dmon/sessions).
    /// </summary>
    private sealed class TempSessionsRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dmon-lazy-session-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    // ── 3.4: end-to-end persistence, including tool calls and tool results ──

    [Fact]
    public async Task Submit_TurnWithNoActiveSession_PersistsToolCallAndResultToNewSessionMessagesJsonl()
    {
        using TempSessionsRoot tempRoot = new();
        ISessionStore sessionStore = BuildRealStore(tempRoot.Path);
        TestEventEmitter sessionEmitter = new();
        SessionHandler sessionHandler = new(sessionStore, sessionEmitter, NullLogger<SessionHandler>.Instance);

        AIFunction tool = MakeStubTool("stub_tool", "42");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("stub_tool", "Computed.");

        (TurnHandler handler, TestEventEmitter turnEmitter) =
            ToolTurnHandlerFactory.Create(provider, tools, sessionHandler, sessionStore);

        Assert.Null(sessionHandler.CurrentSession);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "compute" }, CancellationToken.None);

        SessionMeta? created = sessionHandler.CurrentSession;
        Assert.NotNull(created);
        Assert.Single(turnEmitter.Events.OfType<SessionStartedEvent>());

        string sessionDir = sessionStore.GetSessionDirectory(created!.Id);
        string messagesPath = Path.Combine(sessionDir, "messages.jsonl");
        Assert.True(Directory.Exists(sessionDir), "the implicitly created session's directory must exist on disk");
        Assert.True(File.Exists(messagesPath), "messages.jsonl must exist on disk");

        // Read directly from disk (not from any in-memory record of what was "supposed" to be written).
        IReadOnlyList<SessionLogLine> records = await sessionStore.ReadRecordsAsync(created.Id);
        List<MessageRecord> messageRecords = [.. records.OfType<MessageRecord>()];

        Assert.Contains(messageRecords,
            r => r.Role == "assistant" && r.Parts.OfType<ToolCallPart>().Any(tc => tc.Name == "stub_tool"));
        Assert.Contains(messageRecords,
            r => r.Role == "tool" && r.Parts.OfType<ToolResultPart>().Any());
    }

    // ── 3.5: a core that never runs a turn creates no session directory ─────

    [Fact]
    public void CoreStartedButNeverSubmitsATurn_CreatesNoSessionDirectory()
    {
        using TempSessionsRoot tempRoot = new();
        ISessionStore sessionStore = BuildRealStore(tempRoot.Path);
        TestEventEmitter sessionEmitter = new();
        SessionHandler sessionHandler = new(sessionStore, sessionEmitter, NullLogger<SessionHandler>.Instance);

        // Construct the turn handler with real session handler/store and never submit a turn.
        // This does not model the real core start path — that also runs BootstrapService,
        // which creates the sessions root on a first run regardless of whether a turn is ever
        // submitted. What this proves is narrower: constructing the handlers and running no
        // turn creates no per-session directory (stronger here: the root itself stays absent,
        // because nothing in this harness touches ISessionStore before a turn is submitted).
        StubProviderRegistry providers = new(new StubChatClient());
        (TurnHandler _, TestEventEmitter turnEmitter) =
            TurnHandlerFactory.Create(providers, sessionHandler: sessionHandler, sessionStore: sessionStore);

        Assert.Null(sessionHandler.CurrentSession);
        Assert.Empty(turnEmitter.Events);

        // GetRoot() (which creates the sessions root directory) is only ever reached from inside
        // an ISessionStore operation. If a regression made session creation eager again — at
        // startup rather than lazily on first turn — the root itself would exist by now.
        Assert.False(Directory.Exists(tempRoot.Path),
            "a core that starts and never runs a turn must not create the sessions root, let alone a session directory");
    }

    [Fact]
    public async Task CreateAndActivateAsync_AlwaysSetsCurrentSessionBeforeReturning()
    {
        using TempSessionsRoot tempRoot = new();
        ISessionStore sessionStore = BuildRealStore(tempRoot.Path);
        TestEventEmitter sessionEmitter = new();
        SessionHandler sessionHandler = new(sessionStore, sessionEmitter, NullLogger<SessionHandler>.Instance);

        Assert.Null(sessionHandler.CurrentSession);

        SessionMeta returned = await sessionHandler.CreateAndActivateAsync(agent: null, CancellationToken.None);

        // Over the real seam, CreateAndActivateAsync cannot hand back a SessionMeta without
        // CurrentSession also reflecting it — the two are set together, synchronously, before
        // the method returns. The persist guard's "CurrentSession is null right after creation"
        // branch is therefore unreachable via this path; block 3A's BrokenActivationSessionHandler
        // fake exercises a state the real handler cannot produce.
        Assert.NotNull(sessionHandler.CurrentSession);
        Assert.Equal(returned.Id, sessionHandler.CurrentSession!.Id);
    }

    // ── 3.6: the gateway's create+load handshake leaves no room for the lazy branch ──

    /// <summary>
    /// Replicates the *effect* of <c>NetworkConnectionEndpoint.DriveSessionHandshakeAsync</c> —
    /// an explicit <see cref="SessionCreateCommand"/> followed by a path-less
    /// <see cref="SessionLoadCommand"/>, exactly as the gateway's two-step
    /// <c>session.create</c> → <c>session.load</c> handshake does before any turn can be
    /// submitted — against a real <see cref="SessionHandler"/> over a real
    /// <see cref="SessionStore"/>, then submits a turn and proves:
    /// <list type="bullet">
    ///   <item>no second session directory appears on disk (session-directory count is
    ///     unchanged by the turn — the strongest, hardest-to-fake assertion);</item>
    ///   <item>no <see cref="SessionStartedEvent"/> is emitted (the lazy branch in
    ///     <see cref="TurnHandler.SubmitAsync"/> never fires, asserted structurally by type,
    ///     not merely "the events I expected are present");</item>
    ///   <item>the active session id is unchanged across the turn.</item>
    /// </list>
    ///
    /// This test operates at the core level (<c>SessionHandler</c> + <c>TurnHandler</c>), not
    /// by driving <c>NetworkConnectionEndpoint</c> itself. The existing
    /// <c>Dmon.Network.Tests</c> gateway harness (<c>NetworkCreateE2ETests</c>) backs
    /// <c>DriveSessionHandshakeAsync</c> with an in-process <c>FakeCoreProcess</c> that only
    /// replays scripted stdout lines over a raw stream — there is no real
    /// <c>SessionHandler</c>/<c>TurnHandler</c> on the other end of that fake to submit a turn
    /// against, so driving a turn through that harness would not exercise
    /// <c>TurnHandler.SubmitAsync</c>'s lazy branch at all. This test therefore does NOT prove
    /// that the real <c>DriveSessionHandshakeAsync</c> leaves a session active on the wire —
    /// that is asserted structurally by inspection of its source (it always completes
    /// <c>session.create</c> then <c>session.load</c> before returning, or throws). What this
    /// test proves is the other half of the gateway's claim: GIVEN the state that handshake
    /// leaves behind (an active session from create, reconfirmed by a path-less load), a
    /// submitted turn triggers no implicit creation and no second session.
    /// </summary>
    [Fact]
    public async Task GatewayHandshakeThenTurn_NoImplicitCreation_NoSecondSession()
    {
        using TempSessionsRoot tempRoot = new();
        ISessionStore sessionStore = BuildRealStore(tempRoot.Path);
        TestEventEmitter sessionEmitter = new();
        SessionHandler sessionHandler = new(sessionStore, sessionEmitter, NullLogger<SessionHandler>.Instance);

        // Replicate DriveSessionHandshakeAsync's effect: session.create, then a path-less
        // session.load (mirrors the gateway sending SessionLoadCommand with no Path — see
        // NetworkConnectionEndpoint.DriveSessionHandshakeAsync).
        await sessionHandler.CreateAsync(
            new SessionCreateCommand { Id = "gw-session-create" }, CancellationToken.None);
        await sessionHandler.LoadAsync(
            new SessionLoadCommand { Id = "gw-session-load", Path = null }, CancellationToken.None);

        Assert.Empty(sessionEmitter.Events.OfType<CommandErrorEvent>());
        SessionMeta? handshakeSession = sessionHandler.CurrentSession;
        Assert.NotNull(handshakeSession);

        string sessionsRootAfterHandshake = sessionStore.GetSessionDirectory(handshakeSession!.Id);
        string sessionsRoot = Directory.GetParent(sessionsRootAfterHandshake)!.FullName;
        int sessionDirCountAfterHandshake = Directory.GetDirectories(sessionsRoot).Length;
        Assert.Equal(1, sessionDirCountAfterHandshake);

        // Submit a turn exactly as the gateway-spawned core would receive it over stdio —
        // CurrentSession is already set by the handshake above.
        StubProviderRegistry providers = new(new StubChatClient("hi from gateway"));
        (TurnHandler handler, TestEventEmitter turnEmitter) =
            TurnHandlerFactory.Create(providers, sessionHandler: sessionHandler, sessionStore: sessionStore);

        await handler.SubmitAsync(
            new TurnSubmitCommand { Id = "gw-turn-1", Message = "hello" }, CancellationToken.None);

        // If the lazy branch regressed onto this path, TurnHandler.SubmitAsync would find
        // CurrentSession non-null anyway (a false negative for a naive null check alone) but
        // would still be unreachable here since CurrentSession is set — so the discriminating
        // regression this guards is: a change that re-creates a session unconditionally, or
        // that treats the handshake's session as not "really" active. Either would emit a
        // second SessionStartedEvent and/or grow the on-disk directory count below.
        Assert.Empty(turnEmitter.Events.OfType<SessionStartedEvent>());

        int sessionDirCountAfterTurn = Directory.GetDirectories(sessionsRoot).Length;
        Assert.Equal(sessionDirCountAfterHandshake, sessionDirCountAfterTurn);
        Assert.Equal(1, sessionDirCountAfterTurn);

        Assert.Equal(handshakeSession.Id, sessionHandler.CurrentSession!.Id);
    }

    // ── 3.7: an implicitly created session is a first-class session ─────────

    [Fact]
    public async Task ImplicitlyCreatedSession_ForkAndLoad_SucceedWithSameShapeAsExplicitSession()
    {
        // Path A: implicit creation — submit a turn with no active session.
        using TempSessionsRoot tempRootA = new();
        ISessionStore storeA = BuildRealStore(tempRootA.Path);
        TestEventEmitter sessionEmitterA = new();
        SessionHandler sessionHandlerA = new(storeA, sessionEmitterA, NullLogger<SessionHandler>.Instance);
        StubProviderRegistry providersA = new(new StubChatClient("hi from A"));
        (TurnHandler handlerA, _) = TurnHandlerFactory.Create(providersA, sessionHandler: sessionHandlerA, sessionStore: storeA);

        await handlerA.SubmitAsync(new TurnSubmitCommand { Id = "a1", Message = "hello" }, CancellationToken.None);

        SessionMeta implicitSession = sessionHandlerA.CurrentSession!;
        Assert.NotNull(implicitSession);

        IReadOnlyList<SessionLogLine> recordsA = await storeA.ReadRecordsAsync(implicitSession.Id);
        MessageRecord firstRecordA = Assert.IsType<MessageRecord>(recordsA[0]);

        // Path B: explicit creation via session.create, then a turn against the already-active
        // session (TurnHandler must not lazily create here — CurrentSession is already set).
        using TempSessionsRoot tempRootB = new();
        ISessionStore storeB = BuildRealStore(tempRootB.Path);
        TestEventEmitter sessionEmitterB = new();
        SessionHandler sessionHandlerB = new(storeB, sessionEmitterB, NullLogger<SessionHandler>.Instance);

        await sessionHandlerB.CreateAsync(new SessionCreateCommand { Id = "create-explicit" }, CancellationToken.None);
        SessionMeta explicitSession = sessionHandlerB.CurrentSession!;
        Assert.NotNull(explicitSession);

        StubProviderRegistry providersB = new(new StubChatClient("hi from B"));
        (TurnHandler handlerB, TestEventEmitter turnEmitterB) =
            TurnHandlerFactory.Create(providersB, sessionHandler: sessionHandlerB, sessionStore: storeB);

        await handlerB.SubmitAsync(new TurnSubmitCommand { Id = "b1", Message = "hello" }, CancellationToken.None);

        // Confirms TurnHandler treated session B as already active (no lazy creation there).
        Assert.Empty(turnEmitterB.Events.OfType<SessionStartedEvent>());
        Assert.Equal(explicitSession.Id, sessionHandlerB.CurrentSession!.Id);

        IReadOnlyList<SessionLogLine> recordsB = await storeB.ReadRecordsAsync(explicitSession.Id);
        MessageRecord firstRecordB = Assert.IsType<MessageRecord>(recordsB[0]);

        // ── parity: fork ──
        await sessionHandlerA.ForkAsync(new SessionForkCommand { Id = "fork-a", EntryId = firstRecordA.EntryId }, CancellationToken.None);
        await sessionHandlerB.ForkAsync(new SessionForkCommand { Id = "fork-b", EntryId = firstRecordB.EntryId }, CancellationToken.None);

        Assert.Empty(sessionEmitterA.Events.OfType<CommandErrorEvent>());
        Assert.Empty(sessionEmitterB.Events.OfType<CommandErrorEvent>());

        SessionForkedResultEvent forkedA = Assert.Single(sessionEmitterA.Events.OfType<SessionForkedResultEvent>());
        SessionForkedResultEvent forkedB = Assert.Single(sessionEmitterB.Events.OfType<SessionForkedResultEvent>());

        // Same shape: both forks link back to their source via ParentSession/ForkEntryId, and
        // both inherit their source's (null) Agent.
        Assert.Equal(implicitSession.Id, forkedA.Session.ParentSession);
        Assert.Equal(explicitSession.Id, forkedB.Session.ParentSession);
        Assert.Equal(firstRecordA.EntryId, forkedA.Session.ForkEntryId);
        Assert.Equal(firstRecordB.EntryId, forkedB.Session.ForkEntryId);
        Assert.Equal(implicitSession.Agent, forkedA.Session.Agent);
        Assert.Equal(explicitSession.Agent, forkedB.Session.Agent);
        Assert.True(Directory.Exists(storeA.GetSessionDirectory(forkedA.Session.Id)));
        Assert.True(Directory.Exists(storeB.GetSessionDirectory(forkedB.Session.Id)));

        // ── parity: load ──
        await sessionHandlerA.LoadAsync(
            new SessionLoadCommand { Id = "load-a", Path = storeA.GetSessionDirectory(forkedA.Session.Id) },
            CancellationToken.None);
        await sessionHandlerB.LoadAsync(
            new SessionLoadCommand { Id = "load-b", Path = storeB.GetSessionDirectory(forkedB.Session.Id) },
            CancellationToken.None);

        Assert.Empty(sessionEmitterA.Events.OfType<CommandErrorEvent>());
        Assert.Empty(sessionEmitterB.Events.OfType<CommandErrorEvent>());

        SessionLoadedResultEvent loadedA = Assert.Single(sessionEmitterA.Events.OfType<SessionLoadedResultEvent>());
        SessionLoadedResultEvent loadedB = Assert.Single(sessionEmitterB.Events.OfType<SessionLoadedResultEvent>());

        Assert.Equal(forkedA.Session.Id, loadedA.Session.Id);
        Assert.Equal(forkedB.Session.Id, loadedB.Session.Id);
        Assert.Equal(forkedA.Session.Id, sessionHandlerA.CurrentSession!.Id);
        Assert.Equal(forkedB.Session.Id, sessionHandlerB.CurrentSession!.Id);
    }
}
