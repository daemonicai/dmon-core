using Avalonia.Controls;
using System.Reactive.Linq;

namespace Dmon.Desktop;

/// <summary>
/// Application shell. Shows a boot/fault state until the core is ready,
/// then reveals the conversation area placeholder (Groups 4–5 replace it with the real UI).
/// All reactive state observation happens on RxSchedulers.MainThreadScheduler (injected into
/// <see cref="CoreSessionService"/>), so no manual dispatcher marshalling is needed here.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() : this(null) { }

    public MainWindow(CoreSessionService? sessionService)
    {
        InitializeComponent();

        if (sessionService is null)
            return;

        // Subscribe to boot state changes. Events arrive on MainThreadScheduler
        // (guaranteed by CoreSessionService.StartAsync), so direct control mutation is safe.
        sessionService.State
            .Subscribe(state =>
            {
                switch (state)
                {
                    case CoreState.Booting:
                        BootLabel.Text = "booting...";
                        BootPanel.IsVisible = true;
                        ConversationArea.IsVisible = false;
                        break;

                    case CoreState.Ready:
                        BootPanel.IsVisible = false;
                        ConversationArea.IsVisible = true;
                        break;

                    case CoreState.Faulted:
                        BootLabel.Text = sessionService.FaultMessage is not null
                            ? $"fault: {sessionService.FaultMessage}"
                            : "fault: core failed to start";
                        BootPanel.IsVisible = true;
                        ConversationArea.IsVisible = false;
                        break;
                }
            });
    }
}
