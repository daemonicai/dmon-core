using Dcli;

namespace Dmon.Terminal.Tests.Fakes;

/// <summary>
/// Self-tests that verify FakeTerminal enforces dcli's contract.
/// </summary>
public sealed class FakeTerminalTests
{
    // ── live-block lifecycle ─────────────────────────────────────────────────

    [Fact]
    public void AppendText_AfterCommit_IsNoOp()
    {
        FakeTerminal fake = new();
        ILiveBlock block = fake.Scrollback.BeginLive();
        block.Commit();
        block.AppendText("should be ignored");

        Assert.Equal(2, fake.Calls.Count);
        Assert.IsType<LiveBegun>(fake.Calls[0]);
        Assert.IsType<LiveCommitted>(fake.Calls[1]);
    }

    [Fact]
    public void AppendText_AfterSetContent_IsNoOp()
    {
        FakeTerminal fake = new();
        ILiveBlock block = fake.Scrollback.BeginLive();
        block.SetContent([Line.FromText("replacement")]);
        block.AppendText("should be ignored");

        Assert.Equal(2, fake.Calls.Count);
        Assert.IsType<LiveBegun>(fake.Calls[0]);
        Assert.IsType<LiveSetContent>(fake.Calls[1]);
    }

    [Fact]
    public void AppendText_EmptyString_IsNoOp()
    {
        FakeTerminal fake = new();
        ILiveBlock block = fake.Scrollback.BeginLive();
        block.AppendText("");

        // Only the BeginLive call; the empty AppendText must not be recorded.
        FakeCall single = Assert.Single(fake.Calls);
        Assert.IsType<LiveBegun>(single);
    }

    [Fact]
    public void Commit_IsIdempotent_SecondCallRecordsNothing()
    {
        FakeTerminal fake = new();
        ILiveBlock block = fake.Scrollback.BeginLive();
        block.Commit();
        block.Commit();

        int committedCount = fake.Calls.OfType<LiveCommitted>().Count();
        Assert.Equal(1, committedCount);
    }

    [Fact]
    public void MultipleBlocks_GetDistinctMonotonicIds()
    {
        FakeTerminal fake = new();
        ILiveBlock a = fake.Scrollback.BeginLive();
        ILiveBlock b = fake.Scrollback.BeginLive();
        ILiveBlock c = fake.Scrollback.BeginLive();

        int idA = Assert.IsType<LiveBegun>(fake.Calls[0]).BlockId;
        int idB = Assert.IsType<LiveBegun>(fake.Calls[1]).BlockId;
        int idC = Assert.IsType<LiveBegun>(fake.Calls[2]).BlockId;

        Assert.True(idA < idB && idB < idC);
        Assert.Equal(3, new[] { idA, idB, idC }.Distinct().Count());
    }

    // ── status convenience view ──────────────────────────────────────────────

    [Fact]
    public void CurrentStatus_ReturnsLastSetRowsValue()
    {
        FakeTerminal fake = new();
        IReadOnlyList<Line> first = [Line.FromText("row 1")];
        IReadOnlyList<Line> second = [Line.FromText("row 2"), Line.FromText("row 3")];

        fake.Status.SetRows(first);
        fake.Status.SetRows(second);

        Assert.Equal(second, fake.CurrentStatus);
    }

    // ── dialog scripting ────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAsync_NoHandler_ThrowsInvalidOperationException()
    {
        FakeTerminal fake = new();
        SelectRequest req = new("option A", "option B");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.SelectAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task SelectAsync_RecordsRequestEvenWhenHandlerReturnsCancelled()
    {
        FakeTerminal fake = new();
        SelectRequest req = new("option A");
        fake.OnSelectAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        DialogResult<int> result = await fake.SelectAsync(req, CancellationToken.None);

        Assert.Equal(DialogOutcome.Cancelled, result.Outcome);
        SelectOpened opened = Assert.IsType<SelectOpened>(fake.Calls[0]);
        Assert.Same(req, opened.Request);
    }

    [Fact]
    public async Task SelectAsync_CancellationToken_FlowsThroughToHandler()
    {
        FakeTerminal fake = new();
        SelectRequest req = new("option A");
        fake.OnSelectAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new DialogResult<int>(DialogOutcome.Submitted, 0);
        };

        using CancellationTokenSource cts = new();
        Task<DialogResult<int>> task = fake.SelectAsync(req, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ChoiceAsync_WithoutHandler_Throws()
    {
        FakeTerminal fake = new();
        ChoiceRequest req = new([Line.FromText("yes"), Line.FromText("no")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.ChoiceAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task InputAsync_WithoutHandler_Throws()
    {
        FakeTerminal fake = new();
        InputRequest req = new(Prompt: Line.FromText("Enter value"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.InputAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task ChoiceAsync_RecordsRequestEvenWhenHandlerReturnsCancelled()
    {
        FakeTerminal fake = new();
        ChoiceRequest req = new([Line.FromText("yes"), Line.FromText("no")]);
        fake.OnChoiceAsync = (_, _) =>
            Task.FromResult(new DialogResult<int>(DialogOutcome.Cancelled, default));

        DialogResult<int> result = await fake.ChoiceAsync(req, CancellationToken.None);

        Assert.Equal(DialogOutcome.Cancelled, result.Outcome);
        ChoiceOpened opened = Assert.IsType<ChoiceOpened>(fake.Calls[0]);
        Assert.Same(req, opened.Request);
    }

    [Fact]
    public async Task InputAsync_RecordsRequestEvenWhenHandlerReturnsCancelled()
    {
        FakeTerminal fake = new();
        InputRequest req = new(Prompt: Line.FromText("Enter value"));
        fake.OnInputAsync = (_, _) =>
            Task.FromResult(new DialogResult<string>(DialogOutcome.Cancelled, default!));

        DialogResult<string> result = await fake.InputAsync(req, CancellationToken.None);

        Assert.Equal(DialogOutcome.Cancelled, result.Outcome);
        InputOpened opened = Assert.IsType<InputOpened>(fake.Calls[0]);
        Assert.Same(req, opened.Request);
    }

    // ── autocomplete NotImplementedException ────────────────────────────────

    [Fact]
    public void AutocompleteShow_ThrowsNotImplementedException_WithMessage()
    {
        FakeTerminal fake = new();
        NotImplementedException ex = Assert.Throws<NotImplementedException>(
            () => fake.Autocomplete.Show([]));

        Assert.Contains("HeadlessTerminal", ex.Message);
    }

    // ── events channel ───────────────────────────────────────────────────────

    [Fact]
    public async Task PublishEventAsync_ThenReadAsync_RoundTrips()
    {
        FakeTerminal fake = new();
        InputSubmitted expected = new("hello");

        await fake.PublishEventAsync(expected);
        TerminalEvent actual = await fake.Events.ReadAsync(CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PublishEventAsync_AfterDispose_Throws()
    {
        FakeTerminal fake = new();
        await fake.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fake.PublishEventAsync(new InputSubmitted("x")).AsTask());
    }

    // ── disposal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_IsIdempotent_SecondCallRecordsNothing()
    {
        FakeTerminal fake = new();
        await fake.DisposeAsync();
        await fake.DisposeAsync();

        int disposedCount = fake.Calls.OfType<Disposed>().Count();
        Assert.Equal(1, disposedCount);
    }
}
