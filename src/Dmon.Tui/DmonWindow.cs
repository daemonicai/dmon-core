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
