using Dcli;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;

namespace Dmon.Terminal;

/// <summary>
/// Drives the provider setup wizard: presents provider selection, loops on
/// <see cref="IProviderFactory.GetNextStepAsync"/>, and returns the result
/// needed to emit <see cref="Dmon.Protocol.Commands.ProviderConfigureCommand"/>.
/// </summary>
internal sealed class WizardEngine
{
    private readonly ITerminal _terminal;
    private readonly IReadOnlyList<IProviderFactory> _factories;

    public WizardEngine(ITerminal terminal, IReadOnlyList<IProviderFactory> factories)
    {
        _terminal = terminal;
        _factories = factories;
    }

    /// <summary>
    /// Runs the wizard. Returns a <see cref="WizardResult"/> on success, or
    /// <c>null</c> if the user cancelled.
    /// </summary>
    public async Task<WizardResult?> RunAsync(CancellationToken cancellationToken)
    {
        IProviderFactory? factory = null;
        WizardState state = WizardState.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (factory is null)
            {
                ChooseOneStep selectStep = BuildProviderSelectionStep();
                WizardStepOutcome selectOutcome = await RenderStepAsync(selectStep, cancellationToken)
                    .ConfigureAwait(false);

                if (selectOutcome is WizardStepOutcome.Cancel or WizardStepOutcome.Back)
                    return null;

                string adapterName = selectStep.Options[selectStep.SelectedIndex!.Value].Value;
                factory = _factories.First(
                    f => string.Equals(f.AdapterName, adapterName, StringComparison.OrdinalIgnoreCase));
                state = new WizardState([selectStep]);
                continue;
            }

            WizardStep step = await factory.GetNextStepAsync(state, cancellationToken)
                .ConfigureAwait(false);

            if (step is WizardCompletedStep completed)
            {
                _terminal.Scrollback.Append(new LineBuilder()
                    .Fg(completed.Message, Color.Named(Color.AnsiColor.Green))
                    .Build());
                return BuildResult(factory, state);
            }

            WizardStepOutcome outcome = await RenderStepAsync(step, cancellationToken)
                .ConfigureAwait(false);

            switch (outcome)
            {
                case WizardStepOutcome.Answered:
                    List<WizardStep> newSteps = [.. state.Steps, step];
                    state = state with { Steps = newSteps };
                    break;

                case WizardStepOutcome.Back:
                    if (state.Steps.Count <= 1)
                    {
                        // Back from first factory step → return to provider selection.
                        factory = null;
                        state = WizardState.Empty;
                    }
                    else
                    {
                        List<WizardStep> trimmed = state.Steps.Take(state.Steps.Count - 1).ToList();
                        state = state with { Steps = trimmed };
                    }
                    break;

                case WizardStepOutcome.Cancel:
                    return null;
            }
        }
    }

    private async Task<WizardStepOutcome> RenderStepAsync(WizardStep step, CancellationToken cancellationToken)
    {
        return step switch
        {
            ChooseOneStep s  => await RenderChooseOneAsync(s, cancellationToken).ConfigureAwait(false),
            ChooseManyStep s => await RenderChooseManyAsync(s, cancellationToken).ConfigureAwait(false),
            TextInputStep s  => await RenderTextInputAsync(s, cancellationToken).ConfigureAwait(false),
            YesNoStep s      => await RenderYesNoAsync(s, cancellationToken).ConfigureAwait(false),
            InfoStep s       => RenderInfo(s),
            _                => WizardStepOutcome.Cancel,
        };
    }

    private async Task<WizardStepOutcome> RenderChooseOneAsync(ChooseOneStep step, CancellationToken cancellationToken)
    {
        List<Line> items = step.Options
            .Select(o => Line.FromText(o.Label))
            .ToList();

        DialogResult<int> result = await _terminal.SelectAsync(
            new SelectRequest(
                Items: items,
                Title: new LineBuilder().Bold(step.Prompt).Build(),
                AllowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return WizardStepOutcome.Back;

        if (result.Outcome == DialogOutcome.Cancelled)
            return WizardStepOutcome.Cancel;

        step.SelectedIndex = result.Value;
        return WizardStepOutcome.Answered;
    }

    private async Task<WizardStepOutcome> RenderChooseManyAsync(ChooseManyStep step, CancellationToken cancellationToken)
    {
        // Single-pick fallback: FakeTerminal throws on MultiSelectAsync by design.
        List<Line> items = step.Options
            .Select(o => Line.FromText(o.Label))
            .ToList();

        DialogResult<int> result = await _terminal.SelectAsync(
            new SelectRequest(
                Items: items,
                Title: new LineBuilder().Bold(step.Prompt).Build(),
                AllowBack: true),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Back)
            return WizardStepOutcome.Back;

        if (result.Outcome == DialogOutcome.Cancelled)
            return WizardStepOutcome.Cancel;

        step.SelectedIndices = [result.Value];
        return WizardStepOutcome.Answered;
    }

    private async Task<WizardStepOutcome> RenderTextInputAsync(TextInputStep step, CancellationToken cancellationToken)
    {
        if (step.Default is not null)
        {
            string shown = step.Secret ? new string('*', 8) : step.Default;
            _terminal.Scrollback.Append(new LineBuilder()
                .Dim($"Default: {shown}")
                .Build());
        }

        DialogResult<string> result = await _terminal.InputAsync(
            new InputRequest(
                Prompt: new LineBuilder().Bold(step.Prompt).Build(),
                Default: step.Default,
                IsSecret: step.Secret),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Cancelled)
            return WizardStepOutcome.Cancel;

        string value = result.Value ?? string.Empty;

        if (value.Length == 0 && step.Default is not null)
            value = step.Default;

        if (value.Length == 0 && step.Required)
            return WizardStepOutcome.Cancel;

        step.Value = value.Length > 0 ? value : null;
        return WizardStepOutcome.Answered;
    }

    private async Task<WizardStepOutcome> RenderYesNoAsync(YesNoStep step, CancellationToken cancellationToken)
    {
        List<Line> options = step.Default
            ? [new LineBuilder().Fg("Yes", Color.Named(Color.AnsiColor.Green)).Build(),
               new LineBuilder().Fg("No",  Color.Named(Color.AnsiColor.Red)).Build()]
            : [new LineBuilder().Fg("No",  Color.Named(Color.AnsiColor.Red)).Build(),
               new LineBuilder().Fg("Yes", Color.Named(Color.AnsiColor.Green)).Build()];

        string hint = step.Default ? "[Y/n]" : "[y/N]";

        DialogResult<int> result = await _terminal.ChoiceAsync(
            new ChoiceRequest(
                Options: options,
                Prompt: new LineBuilder().Bold($"{step.Prompt} {hint}").Build()),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == DialogOutcome.Cancelled)
            return WizardStepOutcome.Cancel;

        // When default=true, index 0 = Yes; when default=false, index 0 = No.
        bool selectedYes = step.Default ? result.Value == 0 : result.Value == 1;
        step.Answer = selectedYes;
        return WizardStepOutcome.Answered;
    }

    private WizardStepOutcome RenderInfo(InfoStep step)
    {
        _terminal.Scrollback.Append(new LineBuilder()
            .Dim(step.Prompt)
            .Build());
        return WizardStepOutcome.Answered;
    }

    private ChooseOneStep BuildProviderSelectionStep() =>
        new()
        {
            Id = "adapter",
            Prompt = "Select a provider:",
            Options = _factories
                .Select(f => new WizardOption(f.DisplayName, f.AdapterName))
                .ToList(),
        };

    private static WizardResult BuildResult(IProviderFactory factory, WizardState state)
    {
        ChooseOneStep? modelStep = state.Steps
            .OfType<ChooseOneStep>()
            .FirstOrDefault(s => s.Id == "model");

        string modelId = modelStep is not null
            ? modelStep.Options[modelStep.SelectedIndex!.Value].Value
            : factory.DefaultModelId;

        return new WizardResult(factory.AdapterName, modelId, factory.DefaultEnvVar);
    }
}

internal sealed record WizardResult(string Adapter, string ModelId, string EnvVar);
