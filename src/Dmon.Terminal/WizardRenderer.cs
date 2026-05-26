using Dmon.Abstractions.Wizard;
using Spectre.Console;

namespace Dmon.Terminal;

internal static class WizardRenderer
{
    public static async Task<WizardStepOutcome> RenderAsync(
        WizardStep step, CancellationToken cancellationToken)
    {
        return step switch
        {
            ChooseOneStep s  => await RenderChooseOneAsync(s, cancellationToken).ConfigureAwait(false),
            ChooseManyStep s => await RenderChooseManyAsync(s, cancellationToken).ConfigureAwait(false),
            TextInputStep s  => await RenderTextInputAsync(s, cancellationToken).ConfigureAwait(false),
            YesNoStep s      => await RenderYesNoAsync(s, cancellationToken).ConfigureAwait(false),
            InfoStep s       => await RenderInfoAsync(s, cancellationToken).ConfigureAwait(false),
            _                => WizardStepOutcome.Cancel,
        };
    }

    private static async Task<WizardStepOutcome> RenderChooseOneAsync(
        ChooseOneStep step, CancellationToken cancellationToken)
    {
        string[] labels = step.Options.Select(o => o.Label).ToArray();
        int? choice = await InlinePrompt.ChooseAsync(step.Prompt, labels, cancellationToken)
            .ConfigureAwait(false);

        if (choice is null) return WizardStepOutcome.Cancel;
        if (choice == -1)   return WizardStepOutcome.Back;

        step.SelectedIndex = choice.Value;
        return WizardStepOutcome.Answered;
    }

    private static async Task<WizardStepOutcome> RenderChooseManyAsync(
        ChooseManyStep step, CancellationToken cancellationToken)
    {
        // Single-pick fallback; full multi-select is a future enhancement.
        string[] labels = step.Options.Select(o => o.Label).ToArray();
        int? choice = await InlinePrompt.ChooseAsync(step.Prompt, labels, cancellationToken)
            .ConfigureAwait(false);

        if (choice is null) return WizardStepOutcome.Cancel;
        if (choice == -1)   return WizardStepOutcome.Back;

        step.SelectedIndices = [choice.Value];
        return WizardStepOutcome.Answered;
    }

    private static async Task<WizardStepOutcome> RenderTextInputAsync(
        TextInputStep step, CancellationToken cancellationToken)
    {
        if (step.Default is not null)
        {
            string shown = step.Secret ? new string('*', 8) : Markup.Escape(step.Default);
            AnsiConsole.MarkupLine($"[grey]Default: {shown}[/]");
        }

        string? value = await InlinePrompt.ReadLineAsync(step.Prompt, step.Secret, cancellationToken)
            .ConfigureAwait(false);

        if (value is null) return WizardStepOutcome.Cancel;

        if (value.Length == 0 && step.Default is not null)
            value = step.Default;

        if (value.Length == 0 && step.Required)
            return WizardStepOutcome.Cancel;

        step.Value = value.Length > 0 ? value : null;
        return WizardStepOutcome.Answered;
    }

    private static async Task<WizardStepOutcome> RenderYesNoAsync(
        YesNoStep step, CancellationToken cancellationToken)
    {
        string hint = step.Default ? "Y/n" : "y/N";
        string? value = await InlinePrompt.ReadLineAsync(
            $"{step.Prompt} [{hint}]", secret: false, cancellationToken).ConfigureAwait(false);

        if (value is null) return WizardStepOutcome.Cancel;

        step.Answer = value.Length == 0
            ? step.Default
            : value.Trim().ToLowerInvariant() is "y" or "yes";

        return WizardStepOutcome.Answered;
    }

    private static Task<WizardStepOutcome> RenderInfoAsync(
        InfoStep step, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(step.Prompt)}[/]");
        return Task.FromResult(WizardStepOutcome.Answered);
    }
}
