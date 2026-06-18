using System.Collections.ObjectModel;
using System.Text;
using Dmon.Protocol.Conversation;
using DynamicData;
using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// View-model for a single conversational turn. Wraps a <see cref="MessageRecord"/> and
/// exposes an observable list of <see cref="PartViewModel"/> instances for binding.
///
/// Parts are mutable to support streaming: the in-progress assistant turn accumulates
/// <see cref="TextPartViewModel"/> updates as <c>textDelta</c> events arrive, then
/// settles to a final <see cref="MessageRecord"/> on turn-end.
/// </summary>
public sealed class MessageViewModel : ReactiveObject
{
    private readonly SourceList<PartViewModel> _parts = new();
    private readonly ReadOnlyObservableCollection<PartViewModel> _partsView;

    // Backing accumulator for streaming text; amortises the O(n²) string concatenation
    // in AppendStreamingText to O(1) per append. Null when no streaming is in progress.
    private StringBuilder? _streamingBuffer;

    public MessageViewModel(string role)
    {
        Role = role;
        _parts.Connect()
              .Bind(out _partsView)
              .Subscribe();
    }

    public MessageViewModel(MessageRecord record) : this(record.Role)
    {
        EntryId = record.EntryId;
        Timestamp = record.Timestamp;
        foreach (Part part in record.Parts)
        {
            _parts.Add(PartViewModel.From(part));
        }
    }

    public string Role { get; }
    public string? EntryId { get; private set; }
    public DateTimeOffset? Timestamp { get; private set; }

    /// <summary>Bound collection of part VMs; always on the UI-thread scheduler.</summary>
    public ReadOnlyObservableCollection<PartViewModel> Parts => _partsView;

    /// <summary>
    /// Appends text to the streaming in-progress turn.
    /// Adds a <see cref="TextPartViewModel"/> if none exists; otherwise appends to the last one.
    /// Uses a <see cref="StringBuilder"/> internally so repeated appends are amortised O(1).
    /// </summary>
    internal void AppendStreamingText(string delta)
    {
        if (_parts.Items.LastOrDefault() is TextPartViewModel existing)
        {
            _streamingBuffer ??= new StringBuilder(existing.Text);
            _streamingBuffer.Append(delta);
            existing.Text = _streamingBuffer.ToString();
        }
        else
        {
            _streamingBuffer = new StringBuilder(delta);
            _parts.Add(new TextPartViewModel(new TextPart { Text = delta }));
        }
    }

    /// <summary>
    /// Replaces the part list with the final settled content from <paramref name="record"/>.
    /// Called when history is loaded; clears any streaming-only interim parts and replaces
    /// with the authoritative server-side record.
    /// </summary>
    internal void Settle(MessageRecord record)
    {
        EntryId = record.EntryId;
        Timestamp = record.Timestamp;
        _streamingBuffer = null;
        _parts.Edit(list =>
        {
            list.Clear();
            foreach (Part part in record.Parts)
            {
                list.Add(PartViewModel.From(part));
            }
        });
    }

    /// <summary>
    /// Replaces the part list with a single authoritative text string.
    /// Called on <c>turnEnd</c> with the final <c>content</c> extracted from the wire payload.
    /// Clears any streaming-only interim parts and sets the single settled text.
    /// </summary>
    internal void SettleText(string content)
    {
        _streamingBuffer = null;
        _parts.Edit(list =>
        {
            list.Clear();
            list.Add(new TextPartViewModel(new TextPart { Text = content }));
        });
    }

    /// <summary>
    /// Projects the parts that should be included in an outbound command payload.
    /// <see cref="UnknownPartViewModel"/> and <see cref="UsagePartViewModel"/> are
    /// render-only and MUST NOT appear in any payload sent back to the model.
    /// </summary>
    internal IReadOnlyList<PartViewModel> OutboundParts() =>
        Parts.Where(p => p is not UnknownPartViewModel and not UsagePartViewModel)
             .ToList();
}
