using System.Collections.Concurrent;
using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class CommandDispatcher
{
    // Polymorphic parse options: camelCase + allows the "type" discriminator to appear
    // anywhere in the JSON object (after "id"), which is the standard command wire shape.
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true
    };

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
        CommandParse parse = ParseCommand(line);

        switch (parse)
        {
            case ParseFault fault:
                _logger.LogWarning("Command parse failed [{Code}]: {Message}", fault.Error.Code, fault.Error.Message);
                await _emitter.EmitAsync(fault.Error, cancellationToken).ConfigureAwait(false);
                return;

            case ParsedCommand ok:
                // Long-running interactive commands block on a TCS awaiting a later stdin line.
                // Dispatching them inline would deadlock the reader. Run on a tracked background
                // task so the reader returns immediately and can route the resolving command.
                // (ADR-003: commands are fire-and-forget at the wire level; the reader must not block.)
                if (ok.Command is TurnSubmitCommand or WizardStartCommand)
                {
                    _backgroundTasks.Add(RunGuardedAsync(ok.Command, cancellationToken));
                    return;
                }

                await RunGuardedAsync(ok.Command, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task RunGuardedAsync(Command cmd, CancellationToken cancellationToken)
    {
        try
        {
            await Route(cmd, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or abort — not an error.
        }
        catch (NotImplementedException ex)
        {
            _logger.LogWarning("Command not yet implemented: {Type} — {Message}", cmd.GetType().Name, ex.Message);
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "notImplemented",
                Message = ex.Message,
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error dispatching command {Type}", cmd.GetType().Name);
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "internalError",
                Message = ex.Message,
                Recoverable = false
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task Route(Command cmd, CancellationToken cancellationToken)
    {
        return cmd switch
        {
            TurnSubmitCommand c => _turn.SubmitAsync(c, cancellationToken),
            TurnSteerCommand c => _turn.SteerAsync(c, cancellationToken),
            TurnFollowUpCommand c => _turn.FollowUpAsync(c, cancellationToken),
            TurnAbortCommand c => _turn.AbortAsync(c, cancellationToken),
            ToolConfirmResponseCommand c => _turn.ConfirmResponseAsync(c, cancellationToken),
            UiInputResponseCommand c => _turn.UiInputResponseAsync(c, cancellationToken),
            ModelSetCommand c => _model.SetAsync(c, cancellationToken),
            ModelCycleCommand c => _model.CycleAsync(c, cancellationToken),
            ModelListCommand c => _model.ListAsync(c, cancellationToken),
            ModelModelsCommand c => _model.ModelsAsync(c, cancellationToken),
            SessionCreateCommand c => _session.CreateAsync(c, cancellationToken),
            SessionForkCommand c => _session.ForkAsync(c, cancellationToken),
            SessionCloneCommand c => _session.CloneAsync(c, cancellationToken),
            SessionLoadCommand c => _session.LoadAsync(c, cancellationToken),
            SessionListCommand c => _session.ListAsync(c, cancellationToken),
            SessionSetNameCommand c => _session.SetNameAsync(c, cancellationToken),
            SessionGetStatsCommand c => _session.GetStatsAsync(c, cancellationToken),
            SessionGetMessagesCommand c => _session.GetMessagesAsync(c, cancellationToken),
            SessionCompactCommand => throw new NotImplementedException("session.compact is not yet implemented."),
            ExtensionLoadCommand c => _extension.LoadAsync(c, cancellationToken),
            ExtensionUnloadCommand c => _extension.UnloadAsync(c, cancellationToken),
            ExtensionPromoteCommand c => _extension.PromoteAsync(c, cancellationToken),
            AuthLoginCommand c => _auth.LoginAsync(c, cancellationToken),
            AuthLogoutCommand c => _auth.LogoutAsync(c, cancellationToken),
            AuthStatusCommand c => _auth.StatusAsync(c, cancellationToken),
            ThinkingSetCommand c => _thinking.SetAsync(c, cancellationToken),
            ThinkingCycleCommand c => _thinking.CycleAsync(c, cancellationToken),
            ProviderConfigureCommand c => _providerSetup.ConfigureAsync(c, cancellationToken),
            WizardStartCommand c => _providerSetup.StartWizardAsync(c, cancellationToken),
            WizardAnswerCommand c => _providerSetup.AnswerWizardAsync(c, cancellationToken),
            _ => throw new InvalidOperationException($"No route for {cmd.GetType().Name}.")
        };
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
}
