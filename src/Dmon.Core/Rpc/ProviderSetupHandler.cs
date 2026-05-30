using System.Collections.Concurrent;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Wizard;

namespace Dmon.Core.Rpc;

public class ProviderSetupHandler : IProviderSetupHandler
{
    private readonly IEventEmitter _emitter;
    private readonly IProviderRegistry _registry;
    private readonly IReadOnlyList<IProviderFactory> _factories;

    // At most one active wizard. Keyed by the wizard id (= start command id).
    // The value carries the live TCS that AnswerWizardAsync resolves.
    private volatile WizardSession? _activeSession;

    // Pending answer channels, keyed by wizard id. The engine loop awaits these.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WizardAnswerCommand>> _pendingAnswers = new();

    public ProviderSetupHandler(
        IEventEmitter emitter,
        IProviderRegistry registry,
        IEnumerable<IProviderFactory> factories)
    {
        _emitter = emitter;
        _registry = registry;
        _factories = factories.ToList();
    }

    // ─── provider.configure (direct) ──────────────────────────────────

    public async Task ConfigureAsync(ProviderConfigureCommand command, CancellationToken cancellationToken)
    {
        string? configPath = ResolveConfigPath(command.Scope);
        if (configPath is null)
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = $"Unknown scope '{command.Scope}'. Expected 'global' or 'local'.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.Adapter))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "Adapter must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.ModelId))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "ModelId must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.EnvVar))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = "EnvVar must not be empty.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await PersistProviderAsync(
                command.Adapter, command.ModelId, command.EnvVar,
                command.Scope, configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "provider.configure.failed",
                Message = ex.Message,
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // ─── wizard ───────────────────────────────────────────────────────

    public async Task StartWizardAsync(WizardStartCommand command, CancellationToken cancellationToken)
    {
        // At-most-one-active: reject a second start while one is already in flight.
        if (_activeSession is not null)
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "wizard.alreadyActive",
                Message = "A provider setup wizard is already active. Cancel it before starting a new one.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        string wizardId = command.Id;

        WizardSession session = new(wizardId);
        _activeSession = session;

        try
        {
            await RunWizardAsync(session, wizardId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Clear the session regardless of how the loop exits (cancel, complete, exception).
            if (ReferenceEquals(_activeSession, session))
                _activeSession = null;

            // Discard any pending answer TCS for this wizard (e.g. if cancelled mid-step).
            _pendingAnswers.TryRemove(wizardId, out _);
        }
    }

    public Task AnswerWizardAsync(WizardAnswerCommand command, CancellationToken cancellationToken)
    {
        // Stale-answer guard: ignore answers whose wizardId does not match the active session.
        WizardSession? session = _activeSession;
        if (session is null || session.WizardId != command.WizardId)
            return Task.CompletedTask;

        if (_pendingAnswers.TryGetValue(command.WizardId, out TaskCompletionSource<WizardAnswerCommand>? tcs))
            tcs.TrySetResult(command);

        return Task.CompletedTask;
    }

    // ─── wizard engine loop ───────────────────────────────────────────

    private async Task RunWizardAsync(
        WizardSession session,
        string wizardId,
        CancellationToken cancellationToken)
    {
        IProviderFactory? factory = null;
        WizardState state = WizardState.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WizardStep step;

            if (factory is null)
            {
                // Step 0: provider selection.
                step = BuildProviderSelectionStep();
            }
            else
            {
                step = await factory.GetNextStepAsync(state, cancellationToken).ConfigureAwait(false);

                if (step is WizardCompletedStep)
                {
                    await CompleteWizardAsync(factory, state, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            // Inner loop: emit the step, then retry on invalid answers (re-prompt the same step).
            while (true)
            {
                WizardAnswerCommand answer = await EmitAndAwaitAnswerAsync(
                    wizardId, step, cancellationToken).ConfigureAwait(false);

                switch (answer.Outcome)
                {
                    case WizardAnswerOutcome.Cancel:
                        // Discard — persist nothing.
                        return;

                    case WizardAnswerOutcome.Back:
                        if (factory is null)
                        {
                            // Already at the first step — cancel the wizard.
                            return;
                        }

                        if (state.Steps.Count <= 1)
                        {
                            // Back from the first factory step — return to provider selection.
                            factory = null;
                            state = WizardState.Empty;
                        }
                        else
                        {
                            // Truncate the most-recently answered step and re-invoke GetNextStepAsync.
                            List<WizardStep> trimmed = state.Steps.Take(state.Steps.Count - 1).ToList();
                            state = state with { Steps = trimmed };
                        }
                        goto nextStep;

                    case WizardAnswerOutcome.Answered:
                        if (!ApplyAnswer(step, answer.Value))
                        {
                            // Invalid answer (out-of-range index or unparseable value) — re-prompt
                            // the same step without advancing the factory. Keep the session alive.
                            await _emitter.EmitAsync(new ErrorEvent
                            {
                                Code = "wizard.invalidAnswer",
                                Message = "The answer value is invalid for this step. Please try again.",
                                Recoverable = true
                            }, cancellationToken).ConfigureAwait(false);
                            continue; // inner loop: re-emit same step
                        }

                        if (factory is null)
                        {
                            // Provider-selection step was answered — resolve the factory.
                            ChooseOneStep selectStep = (ChooseOneStep)step;

                            // SelectedIndex is set when ApplyAnswer succeeds, so this is safe.
                            string adapterName = selectStep.Options[selectStep.SelectedIndex!.Value].Value;
                            factory = _factories.FirstOrDefault(
                                f => string.Equals(f.AdapterName, adapterName, StringComparison.OrdinalIgnoreCase));

                            if (factory is null)
                            {
                                await _emitter.EmitAsync(new ErrorEvent
                                {
                                    Code = "wizard.unknownAdapter",
                                    Message = $"No factory registered for adapter '{adapterName}'.",
                                    Recoverable = true
                                }, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            state = new WizardState([step]);
                        }
                        else
                        {
                            List<WizardStep> newSteps = [.. state.Steps, step];
                            state = state with { Steps = newSteps };
                        }
                        goto nextStep;
                }
            }

            nextStep: ;
        }
    }

    private async Task<WizardAnswerCommand> EmitAndAwaitAnswerAsync(
        string wizardId,
        WizardStep step,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<WizardAnswerCommand> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAnswers[wizardId] = tcs;

        try
        {
            await _emitter.EmitAsync(new WizardStepEvent
            {
                WizardId = wizardId,
                Step = step
            }, cancellationToken).ConfigureAwait(false);

            using CancellationTokenRegistration reg = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingAnswers.TryRemove(wizardId, out _);
        }
    }

    // ─── answer application ───────────────────────────────────────────

    // Applies the raw string value from the wire to the concrete step's answer field.
    // Returns true when the value is valid and the answer was applied, false when the
    // value is malformed or out of range (caller re-prompts the step).
    private static bool ApplyAnswer(WizardStep step, string? value)
    {
        switch (step)
        {
            case TextInputStep s:
                s.Value = value;
                return true;

            case ChooseOneStep s:
                if (!int.TryParse(value, out int idx) || idx < 0 || idx >= s.Options.Count)
                    return false;
                s.SelectedIndex = idx;
                return true;

            case ChooseManyStep s:
                try
                {
                    IReadOnlyList<int> indices = WizardAnswerHelper.DecodeChooseManyIndices(value, s.Options.Count);
                    s.SelectedIndices = indices;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }

            case YesNoStep s:
                // "true" / "false" or "yes" / "no" (case-insensitive).
                s.Answer = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                return true;

            case InfoStep:
                // Info steps carry no answer; auto-advance.
                return true;

            default:
                return true;
        }
    }

    // ─── completion ───────────────────────────────────────────────────

    private async Task CompleteWizardAsync(
        IProviderFactory factory,
        WizardState state,
        CancellationToken cancellationToken)
    {
        // Extract wizard result fields from the answered steps — same logic as
        // WizardEngine.BuildResult in Dmon.Terminal.
        ChooseOneStep? modelStep = state.Steps
            .OfType<ChooseOneStep>()
            .FirstOrDefault(s => s.Id == "model");

        string modelId = modelStep is not null
            ? modelStep.Options[modelStep.SelectedIndex!.Value].Value
            : factory.DefaultModelId;

        // Derive the api-key env-var: prefer the factory's default.
        string envVar = factory.DefaultEnvVar;

        string? configPath = ResolveConfigPath("global");
        if (configPath is null)
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "wizard.persistFailed",
                Message = "Could not resolve global config path.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PersistProviderAsync(
            factory.AdapterName, modelId, envVar,
            "global", configPath, cancellationToken).ConfigureAwait(false);
    }

    // ─── shared persistence ───────────────────────────────────────────

    // Single path used by both ConfigureAsync (direct command) and CompleteWizardAsync.
    private async Task PersistProviderAsync(
        string adapter,
        string modelId,
        string envVar,
        string scope,
        string configPath,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(directory);

        string stanzaBody = BuildStanzaBody(adapter, modelId, envVar);

        string content;
        if (!File.Exists(configPath))
        {
            content = "providers:\n" + stanzaBody;
        }
        else
        {
            string existing = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            content = InsertProviderStanza(existing, stanzaBody);
        }

        await File.WriteAllTextAsync(configPath, content, cancellationToken).ConfigureAwait(false);

        _registry.AddDynamicProvider(new ProviderConfig
        {
            Name = adapter,
            Adapter = adapter,
            DefaultModelId = modelId,
            Auth = new ProviderAuthConfig
            {
                Type = "envVar",
                EnvVar = envVar,
            },
        });

        await _emitter.EmitAsync(new ProviderConfiguredEvent
        {
            Adapter = adapter,
            ModelId = modelId,
            Scope = scope
        }, cancellationToken).ConfigureAwait(false);
    }

    // ─── provider-selection step builder ─────────────────────────────

    private ChooseOneStep BuildProviderSelectionStep() =>
        new()
        {
            Id = "adapter",
            Prompt = "Select a provider:",
            Options = _factories
                .Select(f => new WizardOption(f.DisplayName, f.AdapterName))
                .ToList(),
        };

    // ─── path resolution ──────────────────────────────────────────────

    // Returns null for unknown scopes; callers treat null as an error.
    protected virtual string? ResolveConfigPath(string scope)
    {
        if (scope == "global")
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmon",
                "config.yaml");
        }

        if (scope == "local")
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ".dmon", "config.yaml");
        }

        return null;
    }

    // ─── YAML helpers ─────────────────────────────────────────────────

    // The provider stanza body: the indented child lines that live under the
    // top-level `providers:` mapping. The provider key is the adapter name.
    private static string BuildStanzaBody(string adapter, string modelId, string envVar)
    {
        return $"  {adapter}:\n    adapter: {adapter}\n    defaultModelId: {modelId}\n    auth:\n      type: envVar\n      envVar: {envVar}\n";
    }

    // Splices the stanza directly beneath the top-level `providers:` line so the
    // new provider is a child of that mapping.
    private static string InsertProviderStanza(string existing, string stanzaBody)
    {
        string[] lines = existing.Split('\n');
        int providersIndex = Array.FindIndex(lines, IsTopLevelProvidersLine);

        if (providersIndex < 0)
        {
            return existing.TrimEnd() + "\n\nproviders:\n" + stanzaBody;
        }

        IEnumerable<string> head = lines.Take(providersIndex + 1);
        IEnumerable<string> tail = lines.Skip(providersIndex + 1);
        return string.Join('\n', head) + "\n" + stanzaBody + string.Join('\n', tail);
    }

    // A top-level `providers:` mapping key: at column zero and not a comment.
    private static bool IsTopLevelProvidersLine(string line)
    {
        if (line.StartsWith('#') || line.Length == 0 || char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        return line.TrimEnd() == "providers:";
    }

    // ─── session record ───────────────────────────────────────────────

    private sealed class WizardSession(string wizardId)
    {
        public string WizardId { get; } = wizardId;
    }
}
