using Dmon.Protocol.Events;
using Spectre.Console;

namespace Dmon.Console;

/// <summary>
/// Interactive first-run and mid-session provider setup wizard using Spectre.Console prompts.
/// </summary>
public static class SetupWizard
{
    public sealed record SetupWizardResult
    {
        public required string Adapter { get; init; }
        public required string ModelId { get; init; }
        public required string EnvVar { get; init; }
        public required string Scope { get; init; }
    }

    /// <summary>
    /// Shows the provider setup wizard and returns the user's selections.
    /// </summary>
    /// <param name="adapters">All available adapters with env-var detection state.</param>
    /// <param name="isAddProvider">
    /// <c>true</c> for mid-session /add-provider (always full picker + scope step);
    /// <c>false</c> for first-run path (branching based on detected env vars).
    /// </param>
    public static SetupWizardResult Show(IReadOnlyList<AdapterInfo> adapters, bool isAddProvider)
    {
        if (isAddProvider)
            return ShowFullPicker(adapters, includeScope: true);

        IReadOnlyList<AdapterInfo> detected = adapters.Where(a => a.EnvVarDetected).ToList();

        return detected.Count switch
        {
            0 => ShowFullPicker(adapters, includeScope: false),
            1 => ShowSingleDetected(adapters, detected[0]),
            _ => ShowMultiDetected(detected)
        };
    }

    // ── first-run: nothing detected — full picker, no scope step ──────────────

    private static SetupWizardResult ShowFullPicker(IReadOnlyList<AdapterInfo> adapters, bool includeScope)
    {
        AnsiConsole.MarkupLine("[bold]Provider setup[/]");

        AdapterInfo chosen = AnsiConsole.Prompt(
            new SelectionPrompt<AdapterInfo>()
                .Title("Select a provider:")
                .AddChoices(adapters)
                .UseConverter(a => $"{a.Name} ({a.DefaultEnvVar})"));

        string modelId = PromptModelId(chosen.DefaultModelId);
        string envVar = PromptEnvVar(chosen.DefaultEnvVar);
        string scope = includeScope ? PromptScope() : "global";

        return new SetupWizardResult
        {
            Adapter = chosen.Name,
            ModelId = modelId,
            EnvVar = envVar,
            Scope = scope
        };
    }

    // ── first-run: exactly one env var detected — confirmation shortcut ───────

    private static SetupWizardResult ShowSingleDetected(
        IReadOnlyList<AdapterInfo> adapters,
        AdapterInfo detected)
    {
        bool confirmed = AnsiConsole.Prompt(
            new TextPrompt<string>(
                $"Found [green]{detected.DefaultEnvVar}[/] — use [bold]{detected.Name}[/] ([grey]{detected.DefaultModelId}[/])? [[Y/n]]")
                .AllowEmpty()
                .DefaultValue("Y")
                .ValidationErrorMessage("[red]Enter Y or n[/]")
                .Validate(v => v is "" or "Y" or "y" or "N" or "n"
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter Y or n[/]")))
            is "" or "Y" or "y";

        if (!confirmed)
            return ShowFullPicker(adapters, includeScope: false);

        string modelId = PromptModelId(detected.DefaultModelId);

        return new SetupWizardResult
        {
            Adapter = detected.Name,
            ModelId = modelId,
            EnvVar = detected.DefaultEnvVar,
            Scope = "global"
        };
    }

    // ── first-run: multiple env vars detected — picker limited to detected ────

    private static SetupWizardResult ShowMultiDetected(IReadOnlyList<AdapterInfo> detected)
    {
        AnsiConsole.MarkupLine("[bold]Provider setup[/] — [green]detected providers[/]");

        AdapterInfo chosen = AnsiConsole.Prompt(
            new SelectionPrompt<AdapterInfo>()
                .Title("Select a provider:")
                .AddChoices(detected)
                .UseConverter(a => $"{a.Name} ({a.DefaultEnvVar})"));

        string modelId = PromptModelId(chosen.DefaultModelId);

        return new SetupWizardResult
        {
            Adapter = chosen.Name,
            ModelId = modelId,
            EnvVar = chosen.DefaultEnvVar,
            Scope = "global"
        };
    }

    // ── shared prompts ────────────────────────────────────────────────────────

    private static string PromptModelId(string defaultModelId)
    {
        string input = AnsiConsole.Prompt(
            new TextPrompt<string>($"Model ID [[{defaultModelId}]]: ")
                .AllowEmpty());

        return string.IsNullOrWhiteSpace(input) ? defaultModelId : input.Trim();
    }

    private static string PromptEnvVar(string defaultEnvVar)
    {
        string input = AnsiConsole.Prompt(
            new TextPrompt<string>($"API key environment variable [[{defaultEnvVar}]]: ")
                .AllowEmpty());

        return string.IsNullOrWhiteSpace(input) ? defaultEnvVar : input.Trim();
    }

    private static string PromptScope()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Save to:")
                .AddChoices("global (~/.dmon/)", "local (.dmon/)"))
            .StartsWith("global") ? "global" : "local";
    }
}
