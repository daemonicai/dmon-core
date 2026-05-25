using System.Threading.Channels;
using Dmon.Protocol.Events;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Dmon.Tui;

/// <summary>
/// The root window for the dmon TUI. Three zones:
/// <list type="bullet">
///   <item><see cref="ChatOutput"/> — fills all rows except the bottom two.</item>
///   <item>Status bar — second-to-last row; shows model name and thinking/idle state.</item>
///   <item>Input field — last row; fires <see cref="UserInput"/> on Enter.</item>
/// </list>
/// </summary>
internal sealed class DmonWindow : Window
{
    private readonly Label _statusBar;
    private readonly TextField _inputField;
    private string _modelName = string.Empty;

    public DmonWindow()
    {
        Title = "dmon";

        ChatOutput = new ChatOutputView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        _statusBar = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            Text = "  Idle",
        };

        _inputField = new TextField
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };

        _inputField.Accepted += OnInputAccepted;

        Add(ChatOutput, _statusBar, _inputField);

        _inputField.SetFocus();
    }

    // ------------------------------------------------------------------
    // Public API consumed by Group 5 (TuiEventHandler)
    // ------------------------------------------------------------------

    public ChatOutputView ChatOutput { get; }

    public event Action<string>? UserInput;

    public void SetModel(string modelName)
    {
        _modelName = modelName;
        UpdateStatusText(thinking: false);
    }

    public void SetThinking(bool thinking)
    {
        UpdateStatusText(thinking);
    }

    public void LockInput()
    {
        _inputField.Enabled = false;
    }

    public void UnlockInput()
    {
        _inputField.Enabled = true;
        _inputField.SetFocus();
    }

    /// <summary>
    /// Starts a background task that drains <paramref name="events"/> and routes each event
    /// through <paramref name="handler"/>. Stops when the channel completes or
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public void StartEventLoop(
        ChannelReader<Event> events,
        TuiEventHandler handler,
        IApplication app,
        CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            try
            {
                await foreach (Event ev in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await handler.HandleAsync(ev, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down — swallow silently.
            }
            catch (Exception ex)
            {
                app.Invoke(() =>
                {
                    ChatOutput.AddSystemTurn($"[Internal Error] {ex.Message}");
                });
            }
        }, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Wizard
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the add-provider step-dialog wizard and returns the final <see cref="WizardState"/>,
    /// or <c>null</c> if the user cancelled. Must be called on the UI thread (or via
    /// <c>app.Invoke</c> from a background task).
    /// </summary>
    public Task<WizardState?> RunSetupWizardAsync(IApplication app, CancellationToken cancellationToken)
    {
        List<Func<WizardState, Task<WizardState?>>> steps =
        [
            AdapterSelectionStep.Create(app, cancellationToken),
            ModelSelectionStep.Create(app, cancellationToken),
            AuthConfigStep.Create(app, cancellationToken),
        ];
        return WizardRunner.RunAsync(steps, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Private
    // ------------------------------------------------------------------

    private void UpdateStatusText(bool thinking)
    {
        string state = thinking ? "Thinking…" : "Idle";
        _statusBar.Text = string.IsNullOrEmpty(_modelName)
            ? $"  {state}"
            : $"  {_modelName}  {state}";
    }

    private void OnInputAccepted(object? sender, CommandEventArgs e)
    {
        string text = (_inputField.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return;

        _inputField.Text = string.Empty;
        UserInput?.Invoke(text);
    }
}
