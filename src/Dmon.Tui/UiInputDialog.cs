using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Dmon.Tui;

/// <summary>
/// Modal dialog that prompts the user for a text or secret input.
/// </summary>
internal sealed class UiInputDialog : Dialog
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private readonly TextField _inputField;

    private UiInputDialog(string prompt, bool secret)
    {
        Title = "Input Required";
        Width = 60;
        Height = 9;

        Label promptLabel = new()
        {
            Text = prompt,
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
        };
        Add(promptLabel);

        _inputField = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Secret = secret,
        };
        Add(_inputField);

        Button ok = new() { Title = "_OK" };
        Button cancel = new() { Title = "_Cancel" };

        ok.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(_inputField.Text);
            RequestStop();
        };

        cancel.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(null);
            RequestStop();
        };

        AddButton(ok);
        AddButton(cancel);
    }

    /// <summary>
    /// Shows the input dialog and returns the entered value, or null if cancelled.
    /// </summary>
    internal static async Task<string?> ShowAsync(
        IApplication app,
        string prompt,
        string kind,
        CancellationToken cancellationToken)
    {
        bool secret = string.Equals(kind, "secret", StringComparison.OrdinalIgnoreCase);
        UiInputDialog dialog = new(prompt, secret);

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            dialog._tcs.TrySetCanceled(cancellationToken);
            app.Invoke(() => app.RequestStop(dialog));
        });

        await app.RunAsync(dialog, cancellationToken).ConfigureAwait(false);
        dialog._tcs.TrySetResult(null); // Esc / window-close without a button press → Cancel
        return await dialog._tcs.Task.ConfigureAwait(false);
    }
}
