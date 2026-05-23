using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class CommandDispatcher
{
    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ITurnHandler _turn;
    private readonly IModelHandler _model;
    private readonly ISessionHandler _session;
    private readonly IExtensionHandler _extension;
    private readonly IAuthHandler _auth;
    private readonly IThinkingHandler _thinking;
    private readonly IEventEmitter _emitter;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        ITurnHandler turn,
        IModelHandler model,
        ISessionHandler session,
        IExtensionHandler extension,
        IAuthHandler auth,
        IThinkingHandler thinking,
        IEventEmitter emitter,
        ILogger<CommandDispatcher> logger)
    {
        _turn = turn;
        _model = model;
        _session = session;
        _extension = extension;
        _auth = auth;
        _thinking = thinking;
        _emitter = emitter;
        _logger = logger;
    }

    public async Task DispatchAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Received malformed JSON line: {Error}", ex.Message);
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "malformedCommand",
                Message = "Could not parse command JSON.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("type", out JsonElement typeElem))
            {
                await _emitter.EmitAsync(new ErrorEvent
                {
                    Code = "missingType",
                    Message = "Command is missing the 'type' field.",
                    Recoverable = true
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string? type = typeElem.GetString();
            JsonElement root = doc.RootElement;

            try
            {
                await RouteAsync(type, root, cancellationToken).ConfigureAwait(false);
            }
            catch (NotImplementedException ex)
            {
                _logger.LogWarning("Command not yet implemented: {Type} — {Message}", type, ex.Message);
                await _emitter.EmitAsync(new ErrorEvent
                {
                    Code = "notImplemented",
                    Message = ex.Message,
                    Recoverable = true
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error dispatching command {Type}", type);
                await _emitter.EmitAsync(new ErrorEvent
                {
                    Code = "internalError",
                    Message = ex.Message,
                    Recoverable = false
                }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task RouteAsync(string? type, JsonElement element, CancellationToken cancellationToken)
    {
        return type switch
        {
            "turn.submit" => _turn.SubmitAsync(Deserialize<TurnSubmitCommand>(element), cancellationToken),
            "turn.steer" => _turn.SteerAsync(Deserialize<TurnSteerCommand>(element), cancellationToken),
            "turn.followUp" => _turn.FollowUpAsync(Deserialize<TurnFollowUpCommand>(element), cancellationToken),
            "turn.abort" => _turn.AbortAsync(Deserialize<TurnAbortCommand>(element), cancellationToken),
            "tool.confirmResponse" => _turn.ConfirmResponseAsync(Deserialize<ToolConfirmResponseCommand>(element), cancellationToken),
            "ui.inputResponse" => _turn.UiInputResponseAsync(Deserialize<UiInputResponseCommand>(element), cancellationToken),
            "model.set" => _model.SetAsync(Deserialize<ModelSetCommand>(element), cancellationToken),
            "model.cycle" => _model.CycleAsync(Deserialize<ModelCycleCommand>(element), cancellationToken),
            "model.list" => _model.ListAsync(Deserialize<ModelListCommand>(element), cancellationToken),
            "session.create" => _session.CreateAsync(Deserialize<SessionCreateCommand>(element), cancellationToken),
            "session.fork" => _session.ForkAsync(Deserialize<SessionForkCommand>(element), cancellationToken),
            "session.clone" => _session.CloneAsync(Deserialize<SessionCloneCommand>(element), cancellationToken),
            "session.load" => _session.LoadAsync(Deserialize<SessionLoadCommand>(element), cancellationToken),
            "session.list" => _session.ListAsync(Deserialize<SessionListCommand>(element), cancellationToken),
            "session.setName" => _session.SetNameAsync(Deserialize<SessionSetNameCommand>(element), cancellationToken),
            "session.getStats" => _session.GetStatsAsync(Deserialize<SessionGetStatsCommand>(element), cancellationToken),
            "session.getMessages" => _session.GetMessagesAsync(Deserialize<SessionGetMessagesCommand>(element), cancellationToken),
            "extension.load" => _extension.LoadAsync(Deserialize<ExtensionLoadCommand>(element), cancellationToken),
            "extension.unload" => _extension.UnloadAsync(Deserialize<ExtensionUnloadCommand>(element), cancellationToken),
            "extension.promote" => _extension.PromoteAsync(Deserialize<ExtensionPromoteCommand>(element), cancellationToken),
            "auth.login" => _auth.LoginAsync(Deserialize<AuthLoginCommand>(element), cancellationToken),
            "auth.logout" => _auth.LogoutAsync(Deserialize<AuthLogoutCommand>(element), cancellationToken),
            "auth.status" => _auth.StatusAsync(Deserialize<AuthStatusCommand>(element), cancellationToken),
            "thinking.set" => _thinking.SetAsync(Deserialize<ThinkingSetCommand>(element), cancellationToken),
            "thinking.cycle" => _thinking.CycleAsync(Deserialize<ThinkingCycleCommand>(element), cancellationToken),
            _ => UnknownCommandAsync(type, cancellationToken)
        };
    }

    private async Task UnknownCommandAsync(string? type, CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new ErrorEvent
        {
            Code = "unknownCommand",
            Message = $"Unknown command type: '{type}'.",
            Recoverable = true
        }, cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(DeserializerOptions)
           ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
}
