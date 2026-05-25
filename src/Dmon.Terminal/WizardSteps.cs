using Spectre.Console;

namespace Dmon.Terminal;

internal static class AdapterSelectionStep
{
    private static readonly string[] Adapters = ["anthropic", "openai", "gemini"];

    public static Func<WizardState, Task<WizardState?>> Create(CancellationToken cancellationToken)
        => state => RunAsync(state, cancellationToken);

    private static async Task<WizardState?> RunAsync(WizardState state, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]Add Provider — Step 1: Select Adapter[/]").LeftJustified());
        int? choice = await InlinePrompt.ChooseAsync("Adapter:", Adapters, cancellationToken).ConfigureAwait(false);

        if (choice is null) return null;
        if (choice == -1) return WizardState.Back;

        return state with { Adapter = Adapters[choice.Value] };
    }
}

internal static class ModelSelectionStep
{
    private static readonly Dictionary<string, string[]> ModelsByAdapter =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"],
            ["openai"]    = ["gpt-4o", "gpt-4o-mini", "o3"],
            ["gemini"]    = ["gemini-2.5-pro", "gemini-2.5-flash"],
        };

    private static readonly string[] FallbackModels = ["(unknown adapter)"];

    public static Func<WizardState, Task<WizardState?>> Create(CancellationToken cancellationToken)
        => state => RunAsync(state, cancellationToken);

    private static async Task<WizardState?> RunAsync(WizardState state, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]Add Provider — Step 2: Select Model[/]").LeftJustified());
        string[] models = state.Adapter is not null &&
                          ModelsByAdapter.TryGetValue(state.Adapter, out string[]? m)
            ? m : FallbackModels;

        int? choice = await InlinePrompt.ChooseAsync("Model:", models, cancellationToken).ConfigureAwait(false);

        if (choice is null) return null;
        if (choice == -1) return WizardState.Back;

        return state with { ModelId = models[choice.Value] };
    }
}

internal static class AuthConfigStep
{
    private static readonly Dictionary<string, string> EnvVarsByAdapter =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = "ANTHROPIC_API_KEY",
            ["openai"]    = "OPENAI_API_KEY",
            ["gemini"]    = "GEMINI_API_KEY",
        };

    private static readonly string[] ScopeOptions = ["Local (this project)", "Global (all projects)"];

    public static Func<WizardState, Task<WizardState?>> Create(CancellationToken cancellationToken)
        => state => RunAsync(state, cancellationToken);

    private static async Task<WizardState?> RunAsync(WizardState state, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]Add Provider — Step 3: Auth Configuration[/]").LeftJustified());

        string defaultEnvVar = state.Adapter is not null &&
                               EnvVarsByAdapter.TryGetValue(state.Adapter, out string? e)
            ? e : string.Empty;

        AnsiConsole.MarkupLine($"[grey]Default: {Markup.Escape(defaultEnvVar)}[/]");
        string? envVar = await InlinePrompt.ReadLineAsync("Environment variable name", secret: false, cancellationToken).ConfigureAwait(false);

        if (envVar is null) return null;
        if (envVar.Length == 0) envVar = defaultEnvVar;
        if (envVar.Length == 0) return null;

        int? scopeChoice = await InlinePrompt.ChooseAsync("Save to:", ScopeOptions, cancellationToken).ConfigureAwait(false);

        if (scopeChoice is null) return null;
        if (scopeChoice == -1) return WizardState.Back;

        string scope = scopeChoice.Value == 1 ? "global" : "local";
        return state with { EnvVar = envVar, Scope = scope };
    }
}
