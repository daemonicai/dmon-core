using Avalonia.Controls;
using Dmon.Protocol.Enums;

namespace Dmon.Desktop.Views;

/// <summary>
/// Modal dialog for tool-confirmation requests. High-risk tools show a distinct warning banner.
/// Closes with a <see cref="ToolConfirmResult"/>; closing the window without a button yields
/// <see cref="ToolConfirmChoice.Cancelled"/>.
/// </summary>
public partial class ToolConfirmDialog : Window
{
    // Required by the Avalonia XAML resource loader; not used at runtime.
    public ToolConfirmDialog() : this(new ToolConfirmRequest(string.Empty, string.Empty, RiskLevel.None, string.Empty)) { }

    public ToolConfirmDialog(ToolConfirmRequest request)
    {
        InitializeComponent();

        ToolNameLabel.Text = $"tool: {request.Name}";
        ArgsLabel.Text = string.IsNullOrEmpty(request.Args) ? "(no args)" : request.Args;

        if (request.Risk == RiskLevel.High)
            HighRiskBanner.IsVisible = true;

        AllowOnceButton.Click    += (_, _) => Close(new ToolConfirmResult(ToolConfirmChoice.AllowOnce));
        AllowProjectButton.Click += (_, _) => Close(new ToolConfirmResult(ToolConfirmChoice.AllowProject));
        AllowGlobalButton.Click  += (_, _) => Close(new ToolConfirmResult(ToolConfirmChoice.AllowGlobal));
        DenyButton.Click         += (_, _) => Close(new ToolConfirmResult(ToolConfirmChoice.Deny));
    }
}
