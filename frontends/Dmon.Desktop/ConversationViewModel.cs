using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// Default routed screen. Navigated to on startup by <see cref="SessionViewModel"/>.
/// Group 5 adds message-list content; this is the minimal routable shell.
/// </summary>
public sealed class ConversationViewModel : ReactiveObject, IRoutableViewModel
{
    public IScreen HostScreen { get; }
    public string UrlPathSegment => "conversation";

    public ConversationViewModel(IScreen hostScreen)
    {
        HostScreen = hostScreen;
    }
}
