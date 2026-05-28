namespace Dmon.Terminal;

// Post-Phase-4 shape: a pure state mirror above dcli's input layer. dcli owns the OS byte
// source and raw mode; this class mirrors the current buffer text (from InputChanged events)
// and tracks submission history (from InputSubmitted events). The background stdin polling
// thread and Channel<string> from the pre-Phase-4 InputReader are removed.
internal sealed class InputStateLayer
{
    // History cap matches Pi's convention — ample for interactive sessions.
    private const int HistoryCapacity = 100;

    private readonly LinkedList<string> _history = new();

    // volatile: written by the RPC-dispatch task (TurnStart/TurnEnd), read by the UI-dispatch
    // task (DrainAsync on InputSubmitted) — guarantees cross-task visibility without reordering.
    private volatile bool _isLocked;
    public bool IsLocked { get => _isLocked; set => _isLocked = value; }
    public string CurrentBuffer { get; private set; } = string.Empty;
    public IReadOnlyCollection<string> History => _history;

    // Called on every InputChanged event from dcli; mirrors the editor's current text.
    public void OnInputChanged(string text)
    {
        CurrentBuffer = text;
    }

    // Called on every InputSubmitted event from dcli. Appends to History only when not locked
    // and the text is non-whitespace. Locked submissions are silently dropped at the state layer;
    // the dispatch-layer drop (don't forward to core) is handled in ConsoleEventHandler.
    public void OnInputSubmitted(string text)
    {
        if (IsLocked) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_history.Count >= HistoryCapacity)
            _history.RemoveFirst();

        _history.AddLast(text);
        CurrentBuffer = string.Empty;
    }
}
