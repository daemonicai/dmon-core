using System.Collections.Concurrent;
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

    // Polymorphic parse options: camelCase + allows the "type" discriminator to appear
    // anywhere in the JSON object (after "id"), which is the standard command wire shape.
    // Derived from DeserializerOptions so the camelCase policy can't drift independently.
    private static readonly JsonSerializerOptions ParseOptions =
        new(DeserializerOptions) { AllowOutOfOrderMetadataProperties = true };

    private readonly ITurnHandler _turn;
    private readonly IModelHandler _model;
    private readonly ISessionHandler _session;
    private readonly IExtensionHandler _extension;
    private readonly IAuthHandler _auth;
    private readonly IThinkingHandler _thinking;
    private readonly IProviderSetupHandler _providerSetup;
    private readonly IEventEmitter _emitter;
    private readonly ILogger<CommandDispatcher> _logger;

    // Tracked background tasks for long-running interactive commands (turn.submit, wizard.start).
    // Kept so shutdown can observe them and so exceptions are never silently lost.
    private readonly ConcurrentBag<Task> _backgroundTasks = new();

    public CommandDispatcher(
        ITurnHandler turn,
        IModelHandler model,
        ISessionHandler session,
        IExtensionHandler extension,
        IAuthHandler auth,
        IThinkingHandler thinking,
        IProviderSetupHandler providerSetup,
        IEventEmitter emitter,
        ILogger<CommandDispatcher> logger)
    {
        _turn = turn;
        _model = model;
        _session = session;
        _extension = extension;
        _auth = auth;
        _thinking = thinking;
        _providerSetup = providerSetup;
        _emitter = emitter;
        _logger = logger;
    }

    /// <summary>
    /// Waits for all outstanding background tasks to complete (or fault).
    /// Call during graceful shutdown after the reader loop exits.
    /// </summary>
    public Task DrainAsync() => Task.WhenAll(_backgroundTasks);

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

            // Long-running interactive commands block on a TCS awaiting a later stdin line.
            // Dispatching them inline would deadlock the reader. Run on a tracked background
            // task so the reader returns immediately and can route the resolving command.
            //
            // IMPORTANT: JsonElement is a view into doc's memory buffer. We must deserialize
            // to a typed POCO *before* exiting the using block (which disposes doc), so the
            // background task never reads a disposed document.
            if (IsLongRunningCommand(type))
            {
                // Eager deserialization while doc is still alive.
                Func<CancellationToken, Task> work = BuildBackgroundWork(type, root);
                Task backgroundTask = RunBackgroundAsync(type, work, cancellationToken);
                _backgroundTasks.Add(backgroundTask);
                return;
            }

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

    private static bool IsLongRunningCommand(string? type) =>
        type is "turn.submit" or "wizard.start";

    // Eagerly deserializes the long-running command while the JsonDocument is still alive,
    // returning a delegate that captures only the typed POCO (not a JsonElement view).
    // Called inside the using(doc) block so deserialization completes before doc is disposed.
    // The POCO is captured in the closure; the JsonElement is not referenced by the returned Func.
    private Func<CancellationToken, Task> BuildBackgroundWork(string? type, JsonElement element)
    {
        // Deserialize synchronously here — doc is still alive, element is valid.
        return type switch
        {
            "turn.submit" => DeserializeAndBind<TurnSubmitCommand>(element, _turn.SubmitAsync),
            "wizard.start" => DeserializeAndBind<WizardStartCommand>(element, _providerSetup.StartWizardAsync),
            _ => throw new InvalidOperationException($"Unexpected long-running command type: '{type}'.")
        };
    }

    // Deserializes the element immediately and returns a Func closing over the typed POCO.
    // The returned delegate has no reference to the JsonElement or its parent JsonDocument.
    private static Func<CancellationToken, Task> DeserializeAndBind<T>(
        JsonElement element,
        Func<T, CancellationToken, Task> handler)
    {
        T cmd = Deserialize<T>(element); // synchronous — happens while doc is alive
        return (ct) => handler(cmd, ct);
    }

    // Wraps a pre-built work delegate for background execution: surfaces any exception as an
    // ErrorEvent rather than letting it escape unobserved. Accepts a Func so the caller can
    // deserialize eagerly (while JsonDocument is alive) before passing the work here.
    private async Task RunBackgroundAsync(string? type, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or abort — not an error.
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in background command {Type}", type);
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "internalError",
                Message = ex.Message,
                Recoverable = false
            }, cancellationToken).ConfigureAwait(false);
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
            "model.models" => _model.ModelsAsync(Deserialize<ModelModelsCommand>(element), cancellationToken),
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
            "provider.configure" => _providerSetup.ConfigureAsync(Deserialize<ProviderConfigureCommand>(element), cancellationToken),
            "wizard.start" => _providerSetup.StartWizardAsync(Deserialize<WizardStartCommand>(element), cancellationToken),
            "wizard.answer" => _providerSetup.AnswerWizardAsync(Deserialize<WizardAnswerCommand>(element), cancellationToken),
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

    // Total parse stage: never throws for any string input.
    // Returns ParsedCommand on success; ParseFault with the appropriate error code otherwise.
    internal static CommandParse ParseCommand(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new ParseFault(new ErrorEvent
            {
                Code = "malformedCommand",
                Message = "Could not parse command JSON.",
                Recoverable = true
            });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ParseFault(new ErrorEvent
                {
                    Code = "malformedCommand",
                    Message = "Command must be a JSON object.",
                    Recoverable = true
                });
            }

            if (!doc.RootElement.TryGetProperty("type", out _))
            {
                return new ParseFault(new ErrorEvent
                {
                    Code = "missingType",
                    Message = "Command is missing the 'type' field.",
                    Recoverable = true
                });
            }

            try
            {
                Command? cmd = doc.RootElement.Deserialize<Command>(ParseOptions);
                if (cmd is null)
                {
                    return new ParseFault(new ErrorEvent
                    {
                        Code = "malformedCommand",
                        Message = "Command deserialized to null.",
                        Recoverable = true
                    });
                }

                return new ParsedCommand(cmd);
            }
            catch (JsonException ex)
            {
                return new ParseFault(new ErrorEvent
                {
                    Code = "unknownCommand",
                    Message = ex.Message,
                    Recoverable = true
                });
            }
        }
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(DeserializerOptions)
           ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
}
