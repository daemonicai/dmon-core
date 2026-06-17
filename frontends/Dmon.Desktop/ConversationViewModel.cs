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
/// - All state mutations must happen on the injected <paramref name="scheduler"/>
///   (production: <c>RxSchedulers.MainThreadScheduler</c>; tests: <c>TestScheduler</c>).
/// - Incoming <c>messageDelta</c> text events are coalesced with a
///   <see cref="Observable.Buffer{TSource,TBufferClosing}"/> window driven by the injected
///   scheduler so the UI update rate is bounded regardless of burst size.
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

    public ConversationViewModel(IScreen hostScreen, ICoreSession session, IScheduler scheduler)
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
            .ToProperty(this, x => x.IsStreaming, scheduler: scheduler);

        // SendPrompt: disabled while streaming or when prompt is blank.
        IObservable<bool> canSend = this
            .WhenAnyValue(x => x.IsStreaming, x => x.Prompt,
                (streaming, prompt) => !streaming && !string.IsNullOrWhiteSpace(prompt));

        SendPrompt = ReactiveCommand.CreateFromTask(
            async () =>
            {
                string message = Prompt;
                Prompt = string.Empty;
                TurnSubmitCommand command = new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Message = message
                };
                await _session.SendAsync(command).ConfigureAwait(false);
            },
            canSend,
            outputScheduler: scheduler);

        // Coalesce textDelta bursts: buffer for the window, then flatten and apply.
        // Each delta is tagged with the current generation at emission time.
        // The flush discards batches whose generation no longer matches, which closes
        // both the single-turn and cross-turn settle/flush ordering race.
        _eventSubscription = session.Events
            .OfType<MessageDeltaEvent>()
            .Select(e => (Text: ExtractTextDelta(e.Delta), Generation: _currentGeneration))
            .Where(tagged => tagged.Text is not null)
            .Select(tagged => (Text: tagged.Text!, tagged.Generation))
            .Buffer(DeltaCoalesceWindow, scheduler)
            .Where(batch => batch.Count > 0)
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
        session.Events
            .OfType<TurnEndEvent>()
            .Subscribe(e => SettleTurn(e.Message));
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
