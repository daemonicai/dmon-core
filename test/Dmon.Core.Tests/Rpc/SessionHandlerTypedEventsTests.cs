using Dmon.Abstractions.Memory;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Asserts that <see cref="SessionHandler"/> emits the correct typed result events
/// after the typed-command-result-events migration (groups 2 + 4).
/// </summary>
public sealed class SessionHandlerTypedEventsTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static (SessionHandler handler, FakeEventEmitter emitter, FakeSessionStore store)
        Build()
    {
        FakeEventEmitter emitter = new();
        FakeSessionStore store = new();
        SessionHandler handler = new(store, emitter, NullLogger<SessionHandler>.Instance);
        return (handler, emitter, store);
    }

    private static SessionMeta MakeMeta(string id) => new()
    {
        Id       = id,
        Name     = null,
        Created  = DateTimeOffset.UtcNow,
        Modified = DateTimeOffset.UtcNow
    };

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_EmitsSessionCreatedResultEvent()
    {
        var (handler, emitter, _) = Build();
        SessionCreateCommand cmd = new() { Id = "cmd-create-1" };

        await handler.CreateAsync(cmd, CancellationToken.None);

        SessionCreatedResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionCreatedResultEvent>());
        Assert.Equal("cmd-create-1", evt.CommandId);
        Assert.NotNull(evt.Session);
    }

    // ── ForkAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForkAsync_WithActiveSession_EmitsSessionForkedResultEvent()
    {
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.Preset(MakeMeta(tmpDir.SessionId));
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        await handler.LoadAsync(new SessionLoadCommand { Id = "load-1", Path = tmpDir.Path }, CancellationToken.None);
        emitter.Clear();

        SessionForkCommand cmd = new() { Id = "cmd-fork-1", EntryId = "entry-1" };
        await handler.ForkAsync(cmd, CancellationToken.None);

        SessionForkedResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionForkedResultEvent>());
        Assert.Equal("cmd-fork-1", evt.CommandId);
    }

    [Fact]
    public async Task ForkAsync_NoActiveSession_EmitsCommandErrorEvent()
    {
        var (handler, emitter, _) = Build();

        SessionForkCommand cmd = new() { Id = "cmd-fork-fail", EntryId = "entry-1" };
        await handler.ForkAsync(cmd, CancellationToken.None);

        CommandErrorEvent err = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-fork-fail",    err.CommandId);
        Assert.Equal("session.fork",     err.Command);
        Assert.Equal("noActiveSession",  err.Code);
        Assert.NotEmpty(err.Message);
    }

    // ── CloneAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CloneAsync_WithActiveSession_EmitsSessionClonedResultEvent()
    {
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.Preset(MakeMeta(tmpDir.SessionId));
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        await handler.LoadAsync(new SessionLoadCommand { Id = "load-2", Path = tmpDir.Path }, CancellationToken.None);
        emitter.Clear();

        SessionCloneCommand cmd = new() { Id = "cmd-clone-1" };
        await handler.CloneAsync(cmd, CancellationToken.None);

        SessionClonedResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionClonedResultEvent>());
        Assert.Equal("cmd-clone-1", evt.CommandId);
    }

    [Fact]
    public async Task CloneAsync_NoActiveSession_EmitsCommandErrorEvent()
    {
        var (handler, emitter, _) = Build();

        SessionCloneCommand cmd = new() { Id = "cmd-clone-fail" };
        await handler.CloneAsync(cmd, CancellationToken.None);

        CommandErrorEvent err = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-clone-fail",  err.CommandId);
        Assert.Equal("session.clone",   err.Command);
        Assert.Equal("noActiveSession", err.Code);
    }

    // ── LoadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_NoSessionIdOrPath_EmitsCommandErrorWithNoSessionIdOrPathCode()
    {
        var (handler, emitter, _) = Build();

        // No active session, no path on the command → noSessionIdOrPath error.
        SessionLoadCommand cmd = new() { Id = "cmd-load-fail", Path = null };
        await handler.LoadAsync(cmd, CancellationToken.None);

        CommandErrorEvent err = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-load-fail",     err.CommandId);
        Assert.Equal("session.load",      err.Command);
        Assert.Equal("noSessionIdOrPath", err.Code);
        Assert.NotEmpty(err.Message);
    }

    [Fact]
    public async Task LoadAsync_LockedSession_EmitsBothCommandErrorAndErrorEvent()
    {
        // Arrange: create a session directory and pre-acquire the lock from the test.
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        store.Preset(MakeMeta(tmpDir.SessionId));

        // Lock the session from the test side first.
        using FileStream externalLock = new(
            Path.Combine(tmpDir.Path, ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        SessionLoadCommand cmd = new() { Id = "cmd-load-locked", Path = tmpDir.Path };
        await handler.LoadAsync(cmd, CancellationToken.None);

        // Command failure: a CommandErrorEvent correlated by id.
        CommandErrorEvent cmdErr = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-load-locked", cmdErr.CommandId);
        Assert.Equal("session.load",    cmdErr.Command);
        Assert.Equal("sessionLocked",   cmdErr.Code);

        // Ambient notification: an ErrorEvent with code "sessionLocked".
        ErrorEvent ambientErr = Assert.Single(emitter.Emitted.OfType<ErrorEvent>());
        Assert.Equal("sessionLocked", ambientErr.Code);
        Assert.False(ambientErr.Recoverable);
    }

    [Fact]
    public async Task LoadAsync_LockedSession_EmitsCommandErrorBeforeErrorEvent()
    {
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        store.Preset(MakeMeta(tmpDir.SessionId));

        using FileStream externalLock = new(
            Path.Combine(tmpDir.Path, ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        await handler.LoadAsync(new SessionLoadCommand { Id = "cmd-x", Path = tmpDir.Path }, CancellationToken.None);

        List<Event> emitted = emitter.Emitted.ToList();
        int cmdErrIdx    = emitted.FindIndex(e => e is CommandErrorEvent);
        int errorEvtIdx  = emitted.FindIndex(e => e is ErrorEvent);

        Assert.True(cmdErrIdx < errorEvtIdx, "CommandErrorEvent must precede the ambient ErrorEvent.");
    }

    [Fact]
    public async Task LoadAsync_Success_EmitsSessionLoadedResultEvent()
    {
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        store.Preset(MakeMeta(tmpDir.SessionId));

        SessionLoadCommand cmd = new() { Id = "cmd-load-ok", Path = tmpDir.Path };
        await handler.LoadAsync(cmd, CancellationToken.None);

        SessionLoadedResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionLoadedResultEvent>());
        Assert.Equal("cmd-load-ok", evt.CommandId);
        Assert.Equal(tmpDir.SessionId, evt.Session.Id);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_EmitsSessionListResultEvent()
    {
        var (handler, emitter, store) = Build();
        store.AddToList(MakeMeta("s1"), MakeMeta("s2"));

        SessionListCommand cmd = new() { Id = "cmd-list-1" };
        await handler.ListAsync(cmd, CancellationToken.None);

        SessionListResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionListResultEvent>());
        Assert.Equal("cmd-list-1", evt.CommandId);
        Assert.Equal(2, evt.Sessions.Count);
    }

    // ── GetStatsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_EmitsSessionStatsResultEventWithTypedStats()
    {
        var (handler, emitter, _) = Build();

        SessionGetStatsCommand cmd = new() { Id = "cmd-stats-1" };
        await handler.GetStatsAsync(cmd, CancellationToken.None);

        SessionStatsResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionStatsResultEvent>());
        Assert.Equal("cmd-stats-1", evt.CommandId);
        Assert.NotNull(evt.Stats);
        // Stats shape — currently stub values.
        Assert.Equal(0,  evt.Stats.Tokens);
        Assert.Equal(0m, evt.Stats.Cost);
        Assert.Equal(0,  evt.Stats.ContextUsage);
    }

    // ── GetMessagesAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_WithActiveSession_EmitsSessionMessagesResultEvent()
    {
        using TempSessionDir tmpDir = new();
        var (handler, emitter, store) = Build();
        store.MapDirectory(tmpDir.SessionId, tmpDir.Path);
        store.Preset(MakeMeta(tmpDir.SessionId));
        await handler.LoadAsync(new SessionLoadCommand { Id = "load-x", Path = tmpDir.Path }, CancellationToken.None);
        emitter.Clear();

        SessionGetMessagesCommand cmd = new() { Id = "cmd-msg-1" };
        await handler.GetMessagesAsync(cmd, CancellationToken.None);

        SessionMessagesResultEvent evt = Assert.Single(emitter.Emitted.OfType<SessionMessagesResultEvent>());
        Assert.Equal("cmd-msg-1", evt.CommandId);
        Assert.NotNull(evt.Messages);
    }

    [Fact]
    public async Task GetMessagesAsync_NoActiveSession_EmitsCommandErrorEvent()
    {
        var (handler, emitter, _) = Build();

        SessionGetMessagesCommand cmd = new() { Id = "cmd-msg-fail" };
        await handler.GetMessagesAsync(cmd, CancellationToken.None);

        CommandErrorEvent err = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-msg-fail",        err.CommandId);
        Assert.Equal("session.getMessages", err.Command);
        Assert.Equal("noActiveSession",     err.Code);
        Assert.NotEmpty(err.Message);
    }

    // ── SetNameAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetNameAsync_NoActiveSession_EmitsCommandErrorEvent()
    {
        var (handler, emitter, _) = Build();

        SessionSetNameCommand cmd = new() { Id = "cmd-setname-fail", Name = "new-name" };
        await handler.SetNameAsync(cmd, CancellationToken.None);

        CommandErrorEvent err = Assert.Single(emitter.Emitted.OfType<CommandErrorEvent>());
        Assert.Equal("cmd-setname-fail",  err.CommandId);
        Assert.Equal("session.setName",   err.Command);
        Assert.Equal("noActiveSession",   err.Code);
        Assert.NotEmpty(err.Message);
    }

}


// ── Supporting fakes ──────────────────────────────────────────────────────────

internal sealed class FakeSessionStore : ISessionStore
{
    private SessionMeta? _preset;
    private readonly List<SessionMeta> _list = [];
    private readonly Dictionary<string, string> _dirMap = [];

    public void Preset(SessionMeta meta)
    {
        _preset = meta;
        if (!_dirMap.ContainsKey(meta.Id))
            _dirMap[meta.Id] = Path.Combine(Path.GetTempPath(), "dmon-test-sessions", meta.Id);
    }

    public void AddToList(params SessionMeta[] sessions) => _list.AddRange(sessions);

    public void MapDirectory(string sessionId, string path) => _dirMap[sessionId] = path;

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        SessionMeta meta = new()
        {
            Id       = Guid.NewGuid().ToString("N"),
            Name     = name,
            Created  = DateTimeOffset.UtcNow,
            Modified = DateTimeOffset.UtcNow
        };
        _preset = meta;
        return Task.FromResult(meta);
    }

    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_preset is not null && _preset.Id == sessionId)
            return Task.FromResult(_preset);
        // Return a stub if the id matches a mapped dir.
        SessionMeta stub = new() { Id = sessionId, Created = DateTimeOffset.UtcNow, Modified = DateTimeOffset.UtcNow };
        return Task.FromResult(stub);
    }

    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionMeta>>(_list.AsReadOnly());

    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public string GetSessionDirectory(string sessionId) =>
        _dirMap.TryGetValue(sessionId, out string? dir)
            ? dir
            : Path.Combine(Path.GetTempPath(), "dmon-test-sessions", sessionId);

    public Task<SessionMeta> ForkAsync(
        string sourceSessionId,
        string entryId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        SessionMeta forked = new()
        {
            Id            = Guid.NewGuid().ToString("N"),
            Name          = name,
            ParentSession = sourceSessionId,
            ForkEntryId   = entryId,
            Created       = DateTimeOffset.UtcNow,
            Modified      = DateTimeOffset.UtcNow
        };
        return Task.FromResult(forked);
    }

    public Task<SessionMeta> CloneAsync(
        string sourceSessionId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        SessionMeta cloned = new()
        {
            Id            = Guid.NewGuid().ToString("N"),
            Name          = name,
            ParentSession = sourceSessionId,
            Created       = DateTimeOffset.UtcNow,
            Modified      = DateTimeOffset.UtcNow
        };
        return Task.FromResult(cloned);
    }

    public Task<IReadOnlyList<object>> ReadMessagesAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<object>>([]);

    public Task<string> AppendMessageAsync(
        string sessionId,
        string role,
        IReadOnlyList<Part> parts,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Guid.NewGuid().ToString());

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MessageRecord>>(
            messages.Where(m => m.Role != ChatRole.System)
                    .Select(m =>
                    {
                        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                        return new MessageRecord
                        {
                            EntryId = Guid.NewGuid().ToString(),
                            Timestamp = DateTimeOffset.UtcNow,
                            Role = role,
                            Parts = parts
                        };
                    })
                    .ToList());

    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionLogLine>>([]);
}

/// <summary>
/// Creates a temporary directory that acts as a session directory.
/// The directory name becomes the session id and is cleaned up on dispose.
/// </summary>
internal sealed class TempSessionDir : IDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string Path { get; }

    public TempSessionDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dmon-test-lock", SessionId);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }
}
