using Daemon.Core.Session;
using Daemon.Protocol.Commands;
using Daemon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Daemon.Core.Rpc;

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
        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.create",
            Success = true,
            Data = meta
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.fork",
                Success = false,
                Error = "No active session to fork."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta forked = await _store.ForkAsync(
            _currentSession.Id,
            cmd.EntryId,
            name: null,
            cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.fork",
            Success = true,
            Data = forked
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.clone",
                Success = false,
                Error = "No active session to clone."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta cloned = await _store.CloneAsync(
            _currentSession.Id,
            name: null,
            cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.clone",
            Success = true,
            Data = cloned
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
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.load",
                Success = false,
                Error = "No session id or path supplied and no active session."
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

            // Emit the command response so the host's pending request.load completes.
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.load",
                Success = false,
                Error = "Session is locked by another daemon process."
            }, cancellationToken).ConfigureAwait(false);

            // Also emit the named error event for host notification surfaces.
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "sessionLocked",
                Message = $"Session '{sessionId}' is locked by another process.",
                Recoverable = false
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Release previous lock only after the new one is acquired.
        _sessionLock?.Dispose();
        _sessionLock = newLock;

        SessionMeta meta = await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _currentSession = meta;

        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.load",
            Success = true,
            Data = meta
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionMeta> sessions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.list",
            Success = true,
            Data = sessions
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.setName",
                Success = false,
                Error = "No active session."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SessionMeta updated = _currentSession with { Name = cmd.Name };
        await _store.UpdateMetaAsync(updated, cancellationToken).ConfigureAwait(false);
        _currentSession = updated;

        await _emitter.EmitAsync(new SessionUpdatedEvent
        {
            SessionId = updated.Id,
            Title = updated.Name ?? updated.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.getStats",
            Success = true,
            Data = new
            {
                tokens = 0,
                cost = 0m,
                contextUsage = 0,
                currentModel = (string?)null
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            await _emitter.EmitAsync(new ResponseEvent
            {
                RequestId = cmd.Id,
                Command = "session.getMessages",
                Success = false,
                Error = "No active session."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<object> messages = await _store.ReadMessagesAsync(
            _currentSession.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "session.getMessages",
            Success = true,
            Data = messages
        }, cancellationToken).ConfigureAwait(false);
    }
}
