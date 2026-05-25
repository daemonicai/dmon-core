using Dmon.Abstractions.Providers;
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
    public static Func<WizardState, Task<WizardState?>> Create(
        IReadOnlyList<IProviderFactory> factories,
        CancellationToken cancellationToken)
        => state => RunAsync(state, factories, cancellationToken);

    private static async Task<WizardState?> RunAsync(
        WizardState state,
        IReadOnlyList<IProviderFactory> factories,
        CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]Add Provider — Step 3: Select Model[/]").LeftJustified());

        IProviderFactory? factory = factories.FirstOrDefault(
            f => string.Equals(f.AdapterName, state.Adapter, StringComparison.OrdinalIgnoreCase));

        string[] models;
        if (factory is null)
        {
            models = ["(unknown adapter)"];
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Fetching models…[/]");
            IReadOnlyList<ModelInfo> available = await factory
                .GetAvailableModelsAsync(state.ResolvedApiKey, cancellationToken)
                .ConfigureAwait(false);
            models = available.Count > 0
                ? available.Select(m => m.Id).ToArray()
                : ["(no models found)"];
        }

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
        AnsiConsole.Write(new Rule("[bold]Add Provider — Step 2: Auth Configuration[/]").LeftJustified());

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
        string? resolvedKey = Environment.GetEnvironmentVariable(envVar);
        return state with { EnvVar = envVar, Scope = scope, ResolvedApiKey = resolvedKey };
    }
}
