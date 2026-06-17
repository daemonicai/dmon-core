using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Dmon.Desktop;

/// <summary>
/// Top-level screen view-model. Owns the <see cref="RoutingState"/> and hosts one active
/// routed view-model at a time — a degenerate single-tab shell that does not foreclose a
/// future multi-tab design.
///
/// Group 6 additions:
/// - <see cref="ToolConfirmInteraction"/> — raised when a <see cref="ToolConfirmRequestEvent"/>
///   arrives; the view handler shows a modal and returns a <see cref="ToolConfirmResult"/>.
/// - <see cref="UiInputInteraction"/> — raised when a <see cref="UiInputRequestEvent"/> arrives.
/// - <see cref="Reload"/> — ReactiveCommand; CanExecute = !IsStreaming (between turns only).
/// </summary>
public sealed class SessionViewModel : ReactiveObject, IScreen
{
    public RoutingState Router { get; } = new();

    private readonly ICoreSession _session;
    private readonly ObservableAsPropertyHelper<bool> _isStreaming;
    private string? _activeSessionId;

    // Exposed for the view to register interaction handlers.
    public Interaction<ToolConfirmRequest, ToolConfirmResult> ToolConfirmInteraction { get; } = new();
    public Interaction<UiInputRequest, UiInputResult> UiInputInteraction { get; } = new();

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> Reload { get; }

    /// <summary>
    /// Production constructor — called from <see cref="CompositionRoot"/>.
    /// The <paramref name="sessionService"/> provides the event stream already marshalled
    /// onto <see cref="AvaloniaScheduler.Instance"/> (the Avalonia UI-thread dispatcher).
    /// </summary>
    public SessionViewModel(CoreSessionService sessionService)
        : this(sessionService, AvaloniaScheduler.Instance, DefaultScheduler.Instance) { }

    /// <summary>
    /// Testable constructor — accepts <see cref="ICoreSession"/> and a single scheduler that
    /// is used as BOTH the UI scheduler and the coalesce timer scheduler, so that
    /// <c>TestScheduler.AdvanceBy</c> drives the <c>Buffer</c> window AND the
    /// <c>ObserveOn</c> marshal step deterministically.
    /// </summary>
    public SessionViewModel(ICoreSession session, IScheduler scheduler)
        : this(session, scheduler, scheduler) { }

    /// <summary>
    /// Full constructor. Separate schedulers allow the coalescing <c>Buffer</c> window timer
    /// to run on a reliable thread-pool scheduler (<paramref name="coalesceScheduler"/>) while
    /// all VM-state mutations are marshalled onto the UI thread via <paramref name="uiScheduler"/>.
    /// </summary>
    private SessionViewModel(ICoreSession session, IScheduler uiScheduler, IScheduler coalesceScheduler)
    {
        _session = session;

        // IsStreaming: TurnStartEvent → true, TurnEndEvent → false.
        _isStreaming = session.Events
            .Select<Event, bool?>(e => e switch
            {
                TurnStartEvent => true,
                TurnEndEvent   => false,
                _              => null
            })
            .Where(b => b.HasValue)
            .Select(b => b!.Value)
            .StartWith(false)
            .ToProperty(this, x => x.IsStreaming, scheduler: uiScheduler);

        // Reload: only allowed between turns.
        IObservable<bool> canReload = this
            .WhenAnyValue(x => x.IsStreaming, streaming => !streaming);

        Reload = ReactiveCommand.CreateFromTask(
            async () =>
            {
                await session.ReloadAsync().ConfigureAwait(false);

                if (_activeSessionId is not null)
                {
                    SessionLoadCommand load = new()
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Path = _activeSessionId
                    };
                    await session.SendAsync(load).ConfigureAwait(false);
                }
            },
            canReload,
            outputScheduler: uiScheduler);

        // Track the active session id from session-lifecycle result events.
        session.Events.Subscribe(TrackActiveSession);

        // Handle tool-confirm requests: fire-and-forget; protocol mapping at the edge.
        session.Events
            .OfType<ToolConfirmRequestEvent>()
            .Subscribe(e => HandleToolConfirmAsync(e));

        // Handle UI-input requests.
        session.Events
            .OfType<UiInputRequestEvent>()
            .Subscribe(e => HandleUiInputAsync(e));

        ConversationViewModel conversation = new(this, session, uiScheduler, coalesceScheduler);
        // Navigate synchronously on construction so the router is populated before
        // the host renders. Subscribe() is required to trigger the cold observable.
        Router.Navigate.Execute(conversation).Subscribe();
    }

    public bool IsStreaming => _isStreaming.Value;

    // ---------------------------------------------------------------------------
    // Active session tracking — mirrors Terminal's TrackActiveSession
    // ---------------------------------------------------------------------------

    private void TrackActiveSession(Event evt)
    {
        switch (evt)
        {
            case SessionCreatedResultEvent e:
                _activeSessionId = e.Session.Id;
                break;
            case SessionForkedResultEvent e:
                _activeSessionId = e.Session.Id;
                break;
            case SessionClonedResultEvent e:
                _activeSessionId = e.Session.Id;
                break;
            case SessionLoadedResultEvent e:
                _activeSessionId = e.Session.Id;
                break;
        }
    }

    // ---------------------------------------------------------------------------
    // Interaction dispatchers — fire-and-forget async void; protocol mapping at the edge.
    // Interaction<TInput,TOutput>.Handle() returns IObservable<TOutput>; FirstAsync()
    // converts it to an awaitable Task<TOutput>.
    // ---------------------------------------------------------------------------

    private async void HandleToolConfirmAsync(ToolConfirmRequestEvent confirm)
    {
        string argsText = confirm.Args is JsonElement el ? el.ToString() : string.Empty;

        ToolConfirmRequest request = new(
            Name: confirm.Name,
            Args: argsText,
            Risk: confirm.Risk,
            ConfirmId: confirm.ConfirmId);

        ToolConfirmResult result = await ToolConfirmInteraction.Handle(request).FirstAsync();

        string? scope = result.Choice switch
        {
            ToolConfirmChoice.AllowOnce    => "once",
            ToolConfirmChoice.AllowProject => "project",
            ToolConfirmChoice.AllowGlobal  => "global",
            _                              => null
        };

        bool confirmed = result.Choice is ToolConfirmChoice.AllowOnce
                                       or ToolConfirmChoice.AllowProject
                                       or ToolConfirmChoice.AllowGlobal;

        ToolConfirmResponseCommand response = new()
        {
            Id        = confirm.ConfirmId,
            Confirmed = confirmed,
            Cancelled = result.Choice == ToolConfirmChoice.Cancelled,
            Scope     = scope
        };

        await _session.SendAsync(response).ConfigureAwait(false);
    }

    private async void HandleUiInputAsync(UiInputRequestEvent uiInput)
    {
        UiInputRequest request = new(
            Prompt: uiInput.Prompt,
            Kind: uiInput.Kind,
            Options: uiInput.Options,
            EventId: uiInput.EventId);

        UiInputResult result = await UiInputInteraction.Handle(request).FirstAsync();

        UiInputResponseCommand response = new()
        {
            Id        = uiInput.EventId,
            Value     = result.Value,
            Cancelled = result.Cancelled
        };

        await _session.SendAsync(response).ConfigureAwait(false);
    }
}
