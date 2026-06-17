using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using DynamicData;
using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// Routed screen that owns the live message list and the prompt input bar.
///
/// Design constraints (spec Group 5 / design.md Decision 3):
/// - Message list is a DynamicData <see cref="SourceList{T}"/> transformed to a
///   <see cref="ReadOnlyObservableCollection{T}"/> bound in the view.
/// - All VM-state mutations must happen on the injected <paramref name="uiScheduler"/>
///   (production: <see cref="AvaloniaScheduler.Instance"/>; tests: <c>TestScheduler</c>).
/// - Incoming <c>messageDelta</c> text events are coalesced with a
///   <see cref="Observable.Buffer{TSource,TBufferClosing}"/> window driven by the injected
///   <paramref name="coalesceScheduler"/> (production: <see cref="DefaultScheduler.Instance"/>,
///   a thread-pool timer; tests: the same <c>TestScheduler</c> as <paramref name="uiScheduler"/>
///   so that <c>AdvanceBy</c> drives both). The flushed batch is then marshalled onto
///   <paramref name="uiScheduler"/> via <c>ObserveOn</c> before mutating <c>_messages</c>.
///   Keeping the coalescing timer off the UI dispatcher prevents the Heisenbug where
///   <c>Buffer</c>'s window never fires when its scheduler is the live Avalonia dispatcher.
/// - On <c>turnEnd</c> the in-progress assistant message is settled from the final
///   <see cref="MessageRecord"/> carried in the event payload.
/// - <see cref="UnknownPartViewModel"/> parts are rendered but excluded from outbound payloads
///   (see <see cref="MessageViewModel.OutboundParts"/>).
///
/// Group 6.1 additions:
/// - <see cref="Prompt"/> — mutable string bound to the input box.
/// - <see cref="IsStreaming"/> — true while a turn is in progress; derived from
///   <see cref="TurnStartEvent"/>/<see cref="TurnEndEvent"/>.
/// - <see cref="SendPrompt"/> — ReactiveCommand; CanExecute = !IsStreaming AND non-blank Prompt.
/// </summary>
public sealed class ConversationViewModel : ReactiveObject, IRoutableViewModel
{
    // Coalescing window: collect deltas for this duration, then apply as one batch.
    internal static readonly TimeSpan DeltaCoalesceWindow = TimeSpan.FromMilliseconds(50);

    private readonly SourceList<MessageViewModel> _messages = new();
    private readonly ReadOnlyObservableCollection<MessageViewModel> _messagesView;
    private readonly IDisposable _eventSubscription;
    private readonly ICoreSession _session;

    // The assistant message currently being streamed; null when no turn is in progress.
    private MessageViewModel? _inProgressAssistant;

    // Monotonic turn-generation counter. Incremented by SettleTurn.
    // Each delta text is tagged with the generation at emission; the buffer flush
    // compares the tag against _currentGeneration and drops stale batches.
    // This is immune to cross-turn resets: no matter when TurnStart fires, a batch
    // emitted before the most recent SettleTurn will always carry a stale generation.
    private int _currentGeneration;

    private string _prompt = string.Empty;

    public string Prompt
    {
        get => _prompt;
        set => this.RaiseAndSetIfChanged(ref _prompt, value);
    }

    private readonly ObservableAsPropertyHelper<bool> _isStreaming;
    public bool IsStreaming => _isStreaming.Value;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SendPrompt { get; }

    public ConversationViewModel(IScreen hostScreen, ICoreSession session, IScheduler uiScheduler, IScheduler coalesceScheduler)
    {
        HostScreen = hostScreen;
        _session = session;

        _messages.Connect()
                 .Bind(out _messagesView)
                 .Subscribe();

        // IsStreaming: TurnStartEvent → true, TurnEndEvent → false.
        _isStreaming = session.Events
            .Select(e => e switch
            {
                TurnStartEvent => true,
                TurnEndEvent   => false,
                _              => (bool?)null
            })
            .Where(b => b.HasValue)
            .Select(b => b!.Value)
            .StartWith(false)
            .ToProperty(this, x => x.IsStreaming, scheduler: uiScheduler);

        // SendPrompt: disabled while streaming or when prompt is blank.
        IObservable<bool> canSend = this
            .WhenAnyValue(x => x.IsStreaming, x => x.Prompt,
                (streaming, prompt) => !streaming && !string.IsNullOrWhiteSpace(prompt));

        SendPrompt = ReactiveCommand.CreateFromTask(
            async () =>
            {
                string message = Prompt;
                Prompt = string.Empty;

                // Echo the user's message immediately so it's visible before the
                // core responds. TurnEndEvent carries only the ASSISTANT turn, so
                // this never duplicates.
                MessageViewModel userMsg = new("user");
                userMsg.AppendStreamingText(message);
                _messages.Add(userMsg);

                TurnSubmitCommand command = new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Message = message
                };
                await _session.SendAsync(command).ConfigureAwait(false);
            },
            canSend,
            outputScheduler: uiScheduler);

        // Coalesce textDelta bursts: buffer for the window, then marshal onto the UI thread.
        //
        // The Buffer window timer runs on coalesceScheduler (DefaultScheduler.Instance in
        // production — a thread-pool timer that fires reliably regardless of UI-thread load).
        // ObserveOn(uiScheduler) then marshals the flushed batch onto the UI thread before
        // any _messages / _inProgressAssistant mutation. This decoupling prevents the
        // Heisenbug where Buffer's window never fires when its scheduler IS the live Avalonia
        // dispatcher (the UI thread is busy rendering, so the timer callback never gets pumped).
        //
        // Each delta is tagged with the current generation at emission time. The flush discards
        // batches whose generation no longer matches, which closes both the single-turn and
        // cross-turn settle/flush ordering race. Generation reads and writes all occur on the
        // UI thread (via ObserveOn here, and directly in SettleTurn which is called from the
        // TurnEndEvent subscription whose source is already marshalled onto the UI thread by
        // CoreSessionService's ObserveOn in production, and synchronously in tests).
        _eventSubscription = session.Events
            .OfType<MessageDeltaEvent>()
            .Select(e => (Text: ExtractTextDelta(e.Delta), Generation: _currentGeneration))
            .Where(tagged => tagged.Text is not null)
            .Select(tagged => (Text: tagged.Text!, tagged.Generation))
            .Buffer(DeltaCoalesceWindow, coalesceScheduler)
            .Where(batch => batch.Count > 0)
            .ObserveOn(uiScheduler)
            .Subscribe(batch =>
            {
                // All items in a buffered batch share the same generation (the buffer is
                // a time window, not a take-until). Drop any batch whose generation has
                // been superseded by a SettleTurn call that arrived while the window was open.
                if (batch[0].Generation != _currentGeneration)
                    return;

                string coalesced = string.Concat(batch.Select(t => t.Text));
                EnsureInProgressAssistant();
                _inProgressAssistant!.AppendStreamingText(coalesced);
            });

        // Settle the turn on TurnEndEvent.
        // No explicit ObserveOn needed: in production CoreSessionService already ObserveOn's
        // session.Events to AvaloniaScheduler.Instance; in tests Push() fires synchronously.
        session.Events
            .OfType<TurnEndEvent>()
            .Subscribe(e => SettleTurn(e.Message));

        // Surface core status events as "system" messages so nothing is silent.
        session.Events
            .ObserveOn(uiScheduler)
            .Subscribe(e =>
            {
                string? text = e switch
                {
                    ErrorEvent err          => $"[Error] {err.Message}",
                    CommandErrorEvent cmd   => $"[Failed] {cmd.Command}: {cmd.Message}",
                    SystemNoticeEvent note  => $"[Notice] {note.Message}",
                    SetupRequiredEvent      => "[Setup required] No provider configured. Set a provider API key (e.g. ANTHROPIC_API_KEY) and restart.",
                    BootstrapNoticeEvent b  => $"[Session] {b.Path}",
                    _                      => null
                };

                if (text is null)
                    return;

                MessageViewModel msg = new("system");
                msg.AppendStreamingText(text);
                _messages.Add(msg);
            });
    }

    public IScreen HostScreen { get; }
    public string UrlPathSegment => "conversation";

    /// <summary>Bound message list; always updated on the injected scheduler.</summary>
    public ReadOnlyObservableCollection<MessageViewModel> Messages => _messagesView;

    // ---------------------------------------------------------------------------
    // Internal streaming helpers
    // ---------------------------------------------------------------------------

    private void EnsureInProgressAssistant()
    {
        if (_inProgressAssistant is not null)
            return;

        _inProgressAssistant = new MessageViewModel("assistant");
        _messages.Add(_inProgressAssistant);
    }

    private void SettleTurn(object messagePayload)
    {
        // Bump the generation so any buffered deltas still pending for this turn are
        // discarded when the coalescing window flushes.
        _currentGeneration++;

        MessageRecord? record = DeserializeMessageRecord(messagePayload);
        if (record is null)
        {
            _inProgressAssistant = null;
            return;
        }

        if (_inProgressAssistant is not null)
        {
            _inProgressAssistant.Settle(record);
            _inProgressAssistant = null;
        }
        else
        {
            // No streaming took place (e.g. pure tool-call turn); append from the settled record.
            _messages.Add(new MessageViewModel(record));
        }
    }

    // ---------------------------------------------------------------------------
    // Delta extraction — mirrors ConsoleEventHandler.ExtractDeltaText (TUI reference only;
    // code is not shared). The Delta payload arrives as a boxed JsonElement from the RPC
    // deserialiser.
    // ---------------------------------------------------------------------------

    private static string? ExtractTextDelta(object delta)
    {
        if (delta is not JsonElement element)
            return null;
        if (!element.TryGetProperty("type", out JsonElement typeProp))
            return null;
        if (typeProp.GetString() != "textDelta")
            return null;
        if (!element.TryGetProperty("delta", out JsonElement deltaProp))
            return null;
        return deltaProp.GetString();
    }

    // ---------------------------------------------------------------------------
    // MessageRecord deserialisation from the boxed JsonElement in TurnEndEvent.Message
    // ---------------------------------------------------------------------------

    private static MessageRecord? DeserializeMessageRecord(object payload)
    {
        if (payload is not JsonElement element)
            return null;
        try
        {
            return JsonSerializer.Deserialize<MessageRecord>(element, WireSerializerOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
