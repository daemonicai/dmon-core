using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// Top-level screen view-model. Owns the <see cref="RoutingState"/> and hosts one active
/// routed view-model at a time — a degenerate single-tab shell that does not foreclose a
/// future multi-tab design.
/// </summary>
public sealed class SessionViewModel : ReactiveObject, IScreen
{
    public RoutingState Router { get; } = new();

    public SessionViewModel()
    {
        ConversationViewModel conversation = new(this);
        // Navigate synchronously on construction so the router is populated before
        // the host renders. Subscribe() is required to trigger the cold observable.
        Router.Navigate.Execute(conversation).Subscribe();
    }
}
