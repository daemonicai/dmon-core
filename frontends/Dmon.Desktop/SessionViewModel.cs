using System.Reactive.Concurrency;
using Dmon.Protocol.Events;
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

    /// <summary>
    /// Production constructor — called from <see cref="CompositionRoot"/>.
    /// The <paramref name="sessionService"/> provides the event stream already marshalled
    /// onto the scheduler injected into it (production: <c>RxSchedulers.MainThreadScheduler</c>).
    /// </summary>
    public SessionViewModel(CoreSessionService sessionService)
        : this(sessionService.Events, RxSchedulers.MainThreadScheduler) { }

    /// <summary>
    /// Testable constructor — accepts a raw event stream and scheduler directly,
    /// so tests can use a <c>Subject&lt;Event&gt;</c> and <c>TestScheduler</c> without
    /// wiring up a live <see cref="CoreSessionService"/>.
    /// </summary>
    public SessionViewModel(IObservable<Event> events, IScheduler scheduler)
    {
        ConversationViewModel conversation = new(this, events, scheduler);
        // Navigate synchronously on construction so the router is populated before
        // the host renders. Subscribe() is required to trigger the cold observable.
        Router.Navigate.Execute(conversation).Subscribe();
    }
}
