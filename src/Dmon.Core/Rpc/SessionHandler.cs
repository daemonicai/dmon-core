using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;

namespace Dmon.Core.Rpc;

public sealed class SessionHandler : ISessionHandler
{
    private readonly ISessionStore _store;
    private readonly IEventEmitter _emitter;
    private readonly ILogger<SessionHandler> _logger;

    private SessionMeta? _currentSession;
    private SessionLock? _sessionLock;

    public SessionMeta? CurrentSession => _currentSession;

    public SessionHandler(
        ISessionStore store,
        IEventEmitter emitter,
        ILogger<SessionHandler> logger)
    {
        _store = store;
        _emitter = emitter;
        _logger = logger;
    }

    public async Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken)
    {
        SessionMeta meta = await _store.CreateAsync(name: null, cancellationToken).ConfigureAwait(false);
        await _emitter.EmitAsync(new SessionCreatedResultEvent
        {
            CommandId = cmd.Id,
            Session = meta
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command = "session.fork",
                Code    = "noActiveSession",
                Message = "No active session to fork."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta forked = await _store.ForkAsync(
            _currentSession.Id,
            cmd.EntryId,
            name: null,
            cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new SessionForkedResultEvent
        {
            CommandId = cmd.Id,
            Session = forked
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command = "session.clone",
                Code    = "noActiveSession",
                Message = "No active session to clone."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta cloned = await _store.CloneAsync(
            _currentSession.Id,
            name: null,
            cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new SessionClonedResultEvent
        {
            CommandId = cmd.Id,
            Session = cloned
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken)
    {
        string sessionId;

        if (cmd.Path is not null)
        {
            // Derive session id from the last path segment (session dirs are named by id).
            sessionId = Path.GetFileName(cmd.Path.TrimEnd(Path.DirectorySeparatorChar));
        }
        else if (_currentSession is not null)
        {
            sessionId = _currentSession.Id;
        }
        else
        {
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command = "session.load",
                Code    = "noSessionIdOrPath",
                Message = "No session id or path supplied and no active session."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        string sessionDir = _store.GetSessionDirectory(sessionId);

        SessionLock newLock;
        try
        {
            newLock = await SessionLock.AcquireAsync(sessionDir, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionLockedException)
        {
            _logger.LogWarning("Session {SessionId} is locked by another process.", sessionId);

            // Command failure: correlate by id so the host's pending request completes.
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command = "session.load",
                Code    = "sessionLocked",
                Message = "Session is locked by another dmon process."
            }, cancellationToken).ConfigureAwait(false);

            // Ambient notification: kept as a genuine ErrorEvent (no originating command to correlate).
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code        = "sessionLocked",
                Message     = $"Session '{sessionId}' is locked by another process.",
                Recoverable = false
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Release previous lock only after the new one is acquired.
        _sessionLock?.Dispose();
        _sessionLock = newLock;

        SessionMeta meta = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _currentSession = meta;

        await _emitter.EmitAsync(new SessionLoadedResultEvent
        {
            CommandId = cmd.Id,
            Session = meta
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionMeta> sessions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        await _emitter.EmitAsync(new SessionListResultEvent
        {
            CommandId = cmd.Id,
            Sessions  = sessions
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command   = "session.setName",
                Code      = "noActiveSession",
                Message   = "No active session."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta updated = _currentSession with { Name = cmd.Name };
        await _store.UpdateMetaAsync(updated, cancellationToken).ConfigureAwait(false);
        _currentSession = updated;

        await _emitter.EmitAsync(new SessionUpdatedEvent
        {
            SessionId = updated.Id,
            Title     = updated.Name ?? updated.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new SessionStatsResultEvent
        {
            CommandId = cmd.Id,
            Stats     = new SessionStats
            {
                Tokens       = 0,
                Cost         = 0m,
                ContextUsage = 0,
                CurrentModel = null
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new CommandErrorEvent
            {
                CommandId = cmd.Id,
                Command   = "session.getMessages",
                Code      = "noActiveSession",
                Message   = "No active session."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(
            _currentSession.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command   = "session.getMessages",
            Success   = true,
            Data      = messages
        }, cancellationToken).ConfigureAwait(false);
    }
}
