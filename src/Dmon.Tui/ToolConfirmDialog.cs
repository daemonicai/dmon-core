using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Dmon.Tui;

/// <summary>
/// Modal dialog that presents a tool confirmation request to the user.
/// </summary>
internal sealed class ToolConfirmDialog : Dialog
{
    private readonly TaskCompletionSource<ToolPermission?> _tcs = new();

    private ToolConfirmDialog(string toolName, string argsSummary, string riskLevel)
    {
        Title = "Tool Confirmation";
        Width = 62;
        Height = 13;

        bool isHighRisk = string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase);

        int y = 0;

        if (isHighRisk)
        {
            Label warningLabel = new()
            {
                Text = "  !! HIGH RISK !!",
                X = Pos.Center(),
                Y = y,
            };
            Add(warningLabel);
            y++;
        }

        string truncatedArgs = argsSummary.Length > 54 ? argsSummary[..51] + "..." : argsSummary;

        Label nameLabel = new()
        {
            Text = $"Tool: {toolName}",
            X = 1,
            Y = y,
        };
        Add(nameLabel);
        y++;

        Label argsLabel = new()
        {
            Text = $"Args: {truncatedArgs}",
            X = 1,
            Y = y,
        };
        Add(argsLabel);
        y++;

        Label riskLabel = new()
        {
            Text = $"Risk: {riskLevel.ToLowerInvariant()}",
            X = 1,
            Y = y,
        };
        Add(riskLabel);

        Button allowOnce = new() { Title = "Allow _once" };
        Button allowProject = new() { Title = "Allow for _project" };
        Button allowGlobal = new() { Title = "Allow _globally" };
        Button deny = new() { Title = "_Deny" };

        allowOnce.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(ToolPermission.Once);
            RequestStop();
        };

        allowProject.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(ToolPermission.Project);
            RequestStop();
        };

        allowGlobal.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(ToolPermission.Global);
            RequestStop();
        };

        deny.Accepted += (_, _) =>
        {
            _tcs.TrySetResult(null);
            RequestStop();
        };

        AddButton(allowOnce);
        AddButton(allowProject);
        AddButton(allowGlobal);
        AddButton(deny);
    }

    /// <summary>
    /// Shows the tool confirmation dialog and returns the chosen permission scope, or null if denied.
    /// </summary>
    internal static async Task<ToolPermission?> ShowAsync(
        IApplication app,
        string toolName,
        string args,
        string riskLevel,
        CancellationToken cancellationToken)
    {
        ToolConfirmDialog dialog = new(toolName, args, riskLevel);

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            dialog._tcs.TrySetCanceled(cancellationToken);
            app.Invoke(() => app.RequestStop(dialog));
        });

        await app.RunAsync(dialog, cancellationToken).ConfigureAwait(false);
        dialog._tcs.TrySetResult(null); // Esc / window-close without a button press → Deny
        return await dialog._tcs.Task.ConfigureAwait(false);
    }
}
