using Avalonia.Controls;
using Dmon.Protocol.Enums;

namespace Dmon.Desktop.Views;

/// <summary>
/// Modal dialog for UI-input requests. Adapts its layout to the <see cref="UiInputKind"/>:
/// - Text: standard text box.
/// - Secret: password-masked text box.
/// - Select: list box populated from the options list.
/// </summary>
public partial class UiInputDialog : Window
{
    // Required by the Avalonia XAML resource loader; not used at runtime.
    public UiInputDialog() : this(new UiInputRequest(string.Empty, UiInputKind.Text, null, string.Empty)) { }

    public UiInputDialog(UiInputRequest request)
    {
        InitializeComponent();

        PromptLabel.Text = request.Prompt;

        switch (request.Kind)
        {
            case UiInputKind.Secret:
                InputBox.PasswordChar = '*';
                break;

            case UiInputKind.Select:
                InputBox.IsVisible = false;
                OptionsList.IsVisible = true;
                if (request.Options is not null)
                {
                    foreach (string option in request.Options)
                        OptionsList.Items.Add(option);
                }
                break;

            default:
                // UiInputKind.Text — default text box, no modification needed.
                break;
        }

        OkButton.Click += (_, _) =>
        {
            string? value = request.Kind == UiInputKind.Select
                ? OptionsList.SelectedItem as string
                : InputBox.Text;
            Close(new UiInputResult(value, Cancelled: false));
        };

        CancelButton.Click += (_, _) => Close(new UiInputResult(null, Cancelled: true));
    }
}
