using System.Threading.Channels;

namespace Dmon.Terminal.Tests.Fakes;

/// <summary>
/// In-memory implementation of <see cref="Dcli.ITerminal"/> for unit tests.
/// Records every call in <see cref="Calls"/> for assertion.
/// <para>
/// <see cref="Dcli.IAutocomplete.Show"/>, <see cref="Dcli.IAutocomplete.Hide"/>,
/// <see cref="Dcli.ITerminal.MultiSelectAsync"/>, and
/// <see cref="Dcli.IScrollback.BeginCollapsible"/> throw <see cref="NotImplementedException"/>
/// — use <c>HeadlessTerminal</c> for tests that need those.
/// </para>
/// </summary>
public sealed class FakeTerminal : Dcli.ITerminal
{
    // ── call log ────────────────────────────────────────────────────────────

    private readonly List<FakeCall> _calls = [];
    public IReadOnlyList<FakeCall> Calls => _calls;

    // ── live-block tracking ─────────────────────────────────────────────────

    private readonly List<FakeLiveBlock> _liveBlocks = [];
    private int _nextBlockId;

    /// <summary>All live blocks ever opened, including uncommitted ones.</summary>
    public IReadOnlyList<FakeLiveBlock> LiveBlocks => _liveBlocks;

    // ── convenience views ───────────────────────────────────────────────────

    /// <summary>The rows from the last <see cref="StatusSet"/> call, or empty if none recorded.</summary>
    public IReadOnlyList<Dcli.Line> CurrentStatus
    {
        get
        {
            for (int i = _calls.Count - 1; i >= 0; i--)
            {
                if (_calls[i] is StatusSet s) return s.Rows;
            }
            return [];
        }
    }

    /// <summary>Terminal size returned by <see cref="GetTerminalSize"/>.</summary>
    public (int Columns, int Rows) Size { get; set; } = (80, 24);

    // ── dialog scripting ────────────────────────────────────────────────────

    public Func<Dcli.SelectRequest, CancellationToken, Task<Dcli.DialogResult<int>>>? OnSelectAsync { get; set; }
    public Func<Dcli.ChoiceRequest, CancellationToken, Task<Dcli.DialogResult<int>>>? OnChoiceAsync { get; set; }
    public Func<Dcli.InputRequest, CancellationToken, Task<Dcli.DialogResult<string>>>? OnInputAsync { get; set; }

    // ── events channel ──────────────────────────────────────────────────────

    private readonly Channel<Dcli.TerminalEvent> _events =
        Channel.CreateUnbounded<Dcli.TerminalEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public System.Threading.Channels.ChannelReader<Dcli.TerminalEvent> Events => _events.Reader;

    /// <summary>Publishes an event to the <see cref="Events"/> channel for tests that exercise the drain seam.</summary>
    public ValueTask PublishEventAsync(Dcli.TerminalEvent ev, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FakeTerminal));
        cancellationToken.ThrowIfCancellationRequested();
        _events.Writer.TryWrite(ev);
        return ValueTask.CompletedTask;
    }

    // ── disposal ────────────────────────────────────────────────────────────

    private bool _disposed;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _calls.Add(new Disposed());
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ── ITerminal ───────────────────────────────────────────────────────────

    public Dcli.IScrollback Scrollback => _scrollback;
    public Dcli.IInput Input => _input;
    public Dcli.IStatus Status => _status;
    public Dcli.IAutocomplete Autocomplete => _autocomplete;

    private readonly FakeScrollback _scrollback;
    private readonly FakeInput _input;
    private readonly FakeStatus _status;
    private readonly FakeAutocomplete _autocomplete;

    public FakeTerminal()
    {
        _scrollback = new FakeScrollback(this);
        _input = new FakeInput(this);
        _status = new FakeStatus(this);
        _autocomplete = new FakeAutocomplete();
    }

    public (int Columns, int Rows) GetTerminalSize() => Size;

    public Task<Dcli.DialogResult<int>> SelectAsync(
        Dcli.SelectRequest req,
        CancellationToken cancellationToken = default)
    {
        _calls.Add(new SelectOpened(req));
        if (OnSelectAsync is null)
            throw new InvalidOperationException(
                "FakeTerminal.SelectAsync called without a scripted handler — set OnSelectAsync before invoking.");
        return OnSelectAsync(req, cancellationToken);
    }

    public Task<Dcli.DialogResult<int[]>> MultiSelectAsync(
        Dcli.MultiSelectRequest req,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            "FakeTerminal does not implement MultiSelectAsync; use HeadlessTerminal for tests that need it.");

    public Task<Dcli.DialogResult<int>> ChoiceAsync(
        Dcli.ChoiceRequest req,
        CancellationToken cancellationToken = default)
    {
        _calls.Add(new ChoiceOpened(req));
        if (OnChoiceAsync is null)
            throw new InvalidOperationException(
                "FakeTerminal.ChoiceAsync called without a scripted handler — set OnChoiceAsync before invoking.");
        return OnChoiceAsync(req, cancellationToken);
    }

    public Task<Dcli.DialogResult<string>> InputAsync(
        Dcli.InputRequest req,
        CancellationToken cancellationToken = default)
    {
        _calls.Add(new InputOpened(req));
        if (OnInputAsync is null)
            throw new InvalidOperationException(
                "FakeTerminal.InputAsync called without a scripted handler — set OnInputAsync before invoking.");
        return OnInputAsync(req, cancellationToken);
    }

    // ── sub-surfaces ────────────────────────────────────────────────────────

    private sealed class FakeScrollback(FakeTerminal owner) : Dcli.IScrollback
    {
        public void Append(Dcli.Line line) => owner._calls.Add(new ScrollbackAppendLine(line));
        public void Append(string text) => owner._calls.Add(new ScrollbackAppendText(text));

        public Dcli.ILiveBlock BeginLive()
        {
            int id = owner._nextBlockId++;
            FakeLiveBlock block = new(id);
            owner._liveBlocks.Add(block);
            owner._calls.Add(new LiveBegun(id));
            return new FakeLiveBlockHandle(owner, block);
        }

        public Dcli.ICollapsible BeginCollapsible(Dcli.Line summary, IReadOnlyList<Dcli.Line> hiddenLines)
            => throw new NotImplementedException(
                "FakeTerminal does not implement BeginCollapsible; use HeadlessTerminal for tests that need it.");
    }

    private sealed class FakeInput(FakeTerminal owner) : Dcli.IInput
    {
        public void SetText(string text) => owner._calls.Add(new InputSetTextCall(text));
        public void Clear() => owner._calls.Add(new InputClearCall());
    }

    private sealed class FakeStatus(FakeTerminal owner) : Dcli.IStatus
    {
        public void SetRows(params Dcli.Line[] rows) => SetRows((IReadOnlyList<Dcli.Line>)rows);
        public void SetRows(IReadOnlyList<Dcli.Line> rows) => owner._calls.Add(new StatusSet(rows));
    }

    private sealed class FakeAutocomplete : Dcli.IAutocomplete
    {
        public void Show(IReadOnlyList<Dcli.AutocompleteCandidate> candidates)
            => throw new NotImplementedException(
                "FakeTerminal does not implement Autocomplete.Show; use HeadlessTerminal for tests that need it.");

        public void Hide()
            => throw new NotImplementedException(
                "FakeTerminal does not implement Autocomplete.Hide; use HeadlessTerminal for tests that need it.");
    }

    // ── live-block handle ───────────────────────────────────────────────────

    private sealed class FakeLiveBlockHandle(FakeTerminal owner, FakeLiveBlock block) : Dcli.ILiveBlock
    {
        /// <summary>
        /// AppendText is a no-op after Commit or after SetContent — mirrors dcli's LiveBlock contract.
        /// </summary>
        public void AppendText(string text)
        {
            if (text.Length == 0 || block.Committed || block.SetContent is not null) return;
            block.Tokens.Add(text);
            owner._calls.Add(new LiveAppendText(block.Id, text));
        }

        /// <summary>
        /// SetContent is a no-op after Commit.
        /// </summary>
        public void SetContent(IReadOnlyList<Dcli.Line> lines)
        {
            if (block.Committed) return;
            block.SetContent = lines;
            owner._calls.Add(new LiveSetContent(block.Id, lines));
        }

        /// <summary>Commit is idempotent — a second call records nothing.</summary>
        public void Commit()
        {
            if (block.Committed) return;
            block.Committed = true;
            owner._calls.Add(new LiveCommitted(block.Id));
        }
    }
}

/// <summary>Internal state for a live block; mutated by the handle during recording.</summary>
public sealed class FakeLiveBlock(int id)
{
    public int Id { get; } = id;
    public List<string> Tokens { get; } = [];
    public IReadOnlyList<Dcli.Line>? SetContent { get; internal set; }
    public bool Committed { get; internal set; }
}
