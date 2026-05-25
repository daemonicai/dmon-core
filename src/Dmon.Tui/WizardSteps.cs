using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Dmon.Tui;

/// <summary>
/// Factory methods that create each wizard step delegate.
/// Each step shows a Terminal.Gui <see cref="Dialog"/> and returns the updated
/// <see cref="WizardState"/> on success, <see cref="WizardState.Back"/> to navigate
/// back, or <c>null</c> to cancel the whole wizard.
/// </summary>
internal static class AdapterSelectionStep
{
    private static readonly string[] Adapters = ["anthropic", "openai", "gemini"];

    public static Func<WizardState, Task<WizardState?>> Create(IApplication app, CancellationToken cancellationToken)
        => state => ShowAsync(app, state, cancellationToken);

    private static async Task<WizardState?> ShowAsync(
        IApplication app,
        WizardState state,
        CancellationToken cancellationToken)
    {
        AdapterDialog dialog = new();

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            dialog.Tcs.TrySetCanceled(cancellationToken);
            app.Invoke(() => app.RequestStop(dialog));
        });

        await app.RunAsync(dialog, cancellationToken).ConfigureAwait(false);
        dialog.Tcs.TrySetResult(null); // Esc / window-close → cancel
        string? selected = await dialog.Tcs.Task.ConfigureAwait(false);
        return selected is null ? null : state with { Adapter = selected };
    }

    // Result: selected adapter name, or null for cancel.
    private sealed class AdapterDialog : Dialog
    {
        public TaskCompletionSource<string?> Tcs { get; } = new();

        public AdapterDialog()
        {
            Title = "Add Provider — Step 1: Select Adapter";
            Width = 50;
            Height = 12;

            Label label = new()
            {
                Text = "Choose an adapter:",
                X = 1,
                Y = 0,
            };
            Add(label);

            ObservableCollection<string> source = new(Adapters);
            ListView list = new()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = Adapters.Length,
            };
            list.SetSource(source);
            list.SelectedItem = 0;
            Add(list);

            Button selectBtn = new() { Title = "_Select" };
            Button cancelBtn = new() { Title = "_Cancel" };

            selectBtn.Accepted += (_, _) =>
            {
                int idx = list.SelectedItem ?? 0;
                Tcs.TrySetResult(Adapters[idx]);
                RequestStop();
            };

            cancelBtn.Accepted += (_, _) =>
            {
                Tcs.TrySetResult(null);
                RequestStop();
            };

            AddButton(selectBtn);
            AddButton(cancelBtn);
        }
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

    // Sentinel string returned via TCS to signal Back.
    private const string BackSentinel = "\0back";

    public static Func<WizardState, Task<WizardState?>> Create(IApplication app, CancellationToken cancellationToken)
        => state => ShowAsync(app, state, cancellationToken);

    private static async Task<WizardState?> ShowAsync(
        IApplication app,
        WizardState state,
        CancellationToken cancellationToken)
    {
        string[] models = state.Adapter is not null &&
                          ModelsByAdapter.TryGetValue(state.Adapter, out string[]? m)
            ? m
            : FallbackModels;

        ModelDialog dialog = new(state.Adapter ?? "(none)", models);

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            dialog.Tcs.TrySetCanceled(cancellationToken);
            app.Invoke(() => app.RequestStop(dialog));
        });

        await app.RunAsync(dialog, cancellationToken).ConfigureAwait(false);
        dialog.Tcs.TrySetResult(null); // Esc / window-close → cancel
        string? selected = await dialog.Tcs.Task.ConfigureAwait(false);

        if (selected is null)
            return null;
        if (selected == BackSentinel)
            return WizardState.Back;
        return state with { ModelId = selected };
    }

    // Result: selected model ID, BackSentinel, or null for cancel.
    private sealed class ModelDialog : Dialog
    {
        public TaskCompletionSource<string?> Tcs { get; } = new();

        public ModelDialog(string adapterName, string[] models)
        {
            Title = "Add Provider — Step 2: Select Model";
            Width = 56;
            Height = 12;

            Label label = new()
            {
                Text = $"Models for {adapterName}:",
                X = 1,
                Y = 0,
            };
            Add(label);

            ObservableCollection<string> source = new(models);
            ListView list = new()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = models.Length,
            };
            list.SetSource(source);
            list.SelectedItem = 0;
            Add(list);

            Button selectBtn = new() { Title = "_Select" };
            Button backBtn   = new() { Title = "_Back" };
            Button cancelBtn = new() { Title = "_Cancel" };

            selectBtn.Accepted += (_, _) =>
            {
                int idx = list.SelectedItem ?? 0;
                Tcs.TrySetResult(models[idx]);
                RequestStop();
            };

            backBtn.Accepted += (_, _) =>
            {
                Tcs.TrySetResult(BackSentinel);
                RequestStop();
            };

            cancelBtn.Accepted += (_, _) =>
            {
                Tcs.TrySetResult(null);
                RequestStop();
            };

            AddButton(selectBtn);
            AddButton(backBtn);
            AddButton(cancelBtn);
        }
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

    // Sentinel string returned via TCS to signal Back.
    private const string BackSentinel = "\0back";

    public static Func<WizardState, Task<WizardState?>> Create(IApplication app, CancellationToken cancellationToken)
        => state => ShowAsync(app, state, cancellationToken);

    private static async Task<WizardState?> ShowAsync(
        IApplication app,
        WizardState state,
        CancellationToken cancellationToken)
    {
        string defaultEnvVar = state.Adapter is not null &&
                               EnvVarsByAdapter.TryGetValue(state.Adapter, out string? ev)
            ? ev
            : string.Empty;

        AuthDialog dialog = new(defaultEnvVar);

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            dialog.Tcs.TrySetCanceled(cancellationToken);
            app.Invoke(() => app.RequestStop(dialog));
        });

        await app.RunAsync(dialog, cancellationToken).ConfigureAwait(false);
        dialog.Tcs.TrySetResult(null); // Esc / window-close → cancel
        (string envVarName, string scope)? result = await dialog.Tcs.Task.ConfigureAwait(false);

        if (result is null)
            return null;
        if (result.Value.envVarName == BackSentinel)
            return WizardState.Back;
        return state with { EnvVar = result.Value.envVarName, Scope = result.Value.scope };
    }

    // Result: (env var name, scope), BackSentinel env var with empty scope, or null for cancel.
    private sealed class AuthDialog : Dialog
    {
        public TaskCompletionSource<(string EnvVar, string Scope)?> Tcs { get; } = new();

        public AuthDialog(string defaultEnvVar)
        {
            Title = "Add Provider — Step 3: Auth Configuration";
            Width = 60;
            Height = 12;

            Label envVarLabel = new()
            {
                Text = "Environment variable name:",
                X = 1,
                Y = 0,
            };
            Add(envVarLabel);

            TextField field = new()
            {
                Text = defaultEnvVar,
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
            };
            Add(field);

            Label scopeLabel = new()
            {
                Text = "Save to:",
                X = 1,
                Y = 3,
            };
            Add(scopeLabel);

            ObservableCollection<string> scopeOptions = new(["Local (this project)", "Global (all projects)"]);
            ListView scopeList = new()
            {
                X = 1,
                Y = 4,
                Width = Dim.Fill(1),
                Height = 2,
            };
            scopeList.SetSource(scopeOptions);
            scopeList.SelectedItem = 0;
            Add(scopeList);

            Button saveBtn   = new() { Title = "_Save" };
            Button backBtn   = new() { Title = "_Back" };
            Button cancelBtn = new() { Title = "_Cancel" };

            saveBtn.Accepted += (_, _) =>
            {
                string trimmed = (field.Text ?? string.Empty).Trim();
                if (trimmed.Length == 0)
                {
                    Tcs.TrySetResult(null);
                }
                else
                {
                    string scope = scopeList.SelectedItem == 1 ? "global" : "local";
                    Tcs.TrySetResult((trimmed, scope));
                }
                RequestStop();
            };

            backBtn.Accepted += (_, _) =>
            {
                Tcs.TrySetResult((BackSentinel, string.Empty));
                RequestStop();
            };

            cancelBtn.Accepted += (_, _) =>
            {
                Tcs.TrySetResult(null);
                RequestStop();
            };

            AddButton(saveBtn);
            AddButton(backBtn);
            AddButton(cancelBtn);
        }
    }
}
