using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;
using Dmon.Terminal.Tests.Fakes;

namespace Dmon.Terminal.Tests;

/// <summary>
/// Tests for <see cref="ConsoleEventHandler"/> after the typed-command-result-events
/// migration (groups 2 + 4). Verifies session tracking via typed result events and
/// command error display via <see cref="CommandErrorEvent"/>.
/// </summary>
public sealed class SessionTypedEventHandlerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static string LineText(Dcli.Line l) =>
        string.Concat(l.Segments.Select(s => s.Text));

    private static List<string> ScrollbackLines(FakeTerminal fake) =>
        fake.Calls.OfType<ScrollbackAppendLine>().Select(c => LineText(c.Line)).ToList();

    private static ConsoleEventHandler BuildHandler(
        FakeTerminal fake,
        List<Command> sentCommands,
        CancellationTokenSource cts)
    {
        Func<Command, CancellationToken, Task> send = (cmd, _) =>
        {
            sentCommands.Add(cmd);
            return Task.CompletedTask;
        };

        TerminalRenderer renderer = new(fake);
        InputStateLayer input = new();
        return new ConsoleEventHandler(renderer, input, send, cts, () => { }, fake);
    }

    private static SessionMeta MakeMeta(string id) => new()
    {
        Id       = id,
        Name     = null,
        Created  = DateTimeOffset.UtcNow,
        Modified = DateTimeOffset.UtcNow
    };

    // ── ActiveSessionId tracking — typed events ───────────────────────────────

    [Fact]
    public async Task SessionCreatedResult_SetsActiveSessionId()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionCreatedResultEvent evt = new() { CommandId = "c1", Session = MakeMeta("session-a") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Equal("session-a", handler.ActiveSessionId);
    }

    [Fact]
    public async Task SessionForkedResult_SetsActiveSessionId()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionForkedResultEvent evt = new() { CommandId = "c2", Session = MakeMeta("session-b") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Equal("session-b", handler.ActiveSessionId);
    }

    [Fact]
    public async Task SessionClonedResult_SetsActiveSessionId()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionClonedResultEvent evt = new() { CommandId = "c3", Session = MakeMeta("session-c") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Equal("session-c", handler.ActiveSessionId);
    }

    [Fact]
    public async Task SessionLoadedResult_SetsActiveSessionId()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionLoadedResultEvent evt = new() { CommandId = "c4", Session = MakeMeta("session-d") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Equal("session-d", handler.ActiveSessionId);
    }

    // ── session-start display — design D6 ────────────────────────────────────

    [Fact]
    public async Task SessionCreatedResult_DisplaysSessionLine()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionCreatedResultEvent evt = new() { CommandId = "c1", Session = MakeMeta("session-a") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = ScrollbackLines(fake);
        Assert.Contains(lines, l => l.Contains("session-a"));
    }

    [Fact]
    public async Task SessionForkedResult_DisplaysSessionLine()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionForkedResultEvent evt = new() { CommandId = "c2", Session = MakeMeta("session-b") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = ScrollbackLines(fake);
        Assert.Contains(lines, l => l.Contains("session-b"));
    }

    [Fact]
    public async Task SessionClonedResult_DisplaysSessionLine()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionClonedResultEvent evt = new() { CommandId = "c3", Session = MakeMeta("session-c") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = ScrollbackLines(fake);
        Assert.Contains(lines, l => l.Contains("session-c"));
    }

    [Fact]
    public async Task SessionLoadedResult_DisplaysSessionLine()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionLoadedResultEvent evt = new() { CommandId = "c4", Session = MakeMeta("session-d") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = ScrollbackLines(fake);
        Assert.Contains(lines, l => l.Contains("session-d"));
    }

    [Fact]
    public async Task SessionStartedEvent_DisplaysSessionLine()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionStartedEvent evt = new() { Session = MakeMeta("session-e") };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Equal("session-e", handler.ActiveSessionId);
        IEnumerable<string> lines = ScrollbackLines(fake);
        Assert.Contains(lines, l => l.Contains("session-e"));
    }

    [Fact]
    public async Task SessionStartedEvent_DisplaysSameFormAsSessionCreatedResult()
    {
        // task 4.2: explicit (/new -> session.createResult) and implicit (sessionStarted)
        // session start must be indistinguishable in wording to the user.
        FakeTerminal createdFake = new();
        ConsoleEventHandler createdHandler = BuildHandler(createdFake, [], new CancellationTokenSource());
        await createdHandler.HandleRpcEventAsync(
            (Event)new SessionCreatedResultEvent { CommandId = "c1", Session = MakeMeta("same-id") },
            CancellationToken.None);

        FakeTerminal startedFake = new();
        ConsoleEventHandler startedHandler = BuildHandler(startedFake, [], new CancellationTokenSource());
        await startedHandler.HandleRpcEventAsync(
            (Event)new SessionStartedEvent { Session = MakeMeta("same-id") },
            CancellationToken.None);

        string createdLine = Assert.Single(ScrollbackLines(createdFake));
        string startedLine = Assert.Single(ScrollbackLines(startedFake));
        Assert.Equal(createdLine, startedLine);
    }

    [Fact]
    public async Task SessionCreatedResult_EmptyId_DoesNotDisplayOrTrack()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionCreatedResultEvent evt = new() { CommandId = "c1", Session = MakeMeta(string.Empty) };
        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Null(handler.ActiveSessionId);
        Assert.Empty(ScrollbackLines(fake));
    }

    [Fact]
    public async Task SessionCreatedResult_OverwritesPreviousActiveSessionId()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        await handler.HandleRpcEventAsync(
            (Event)new SessionCreatedResultEvent { CommandId = "c1", Session = MakeMeta("first") },
            CancellationToken.None);

        await handler.HandleRpcEventAsync(
            (Event)new SessionLoadedResultEvent { CommandId = "c2", Session = MakeMeta("second") },
            CancellationToken.None);

        Assert.Equal("second", handler.ActiveSessionId);
    }

    // ── CommandErrorEvent display ─────────────────────────────────────────────

    [Fact]
    public async Task CommandErrorEvent_RendersFailedLineWithCommandAndMessage()
    {
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        CommandErrorEvent evt = new()
        {
            CommandId = "req-x",
            Command   = "session.fork",
            Code      = "noActiveSession",
            Message   = "No active session to fork."
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(lines, l =>
            l.Contains("[Failed]") &&
            l.Contains("session.fork") &&
            l.Contains("No active session to fork."));
    }

    [Fact]
    public async Task CommandErrorEvent_DoesNotCancelCts()
    {
        // Command errors are not fatal — the host stays alive.
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        CommandErrorEvent evt = new()
        {
            CommandId = "req-y",
            Command   = "session.clone",
            Code      = "noActiveSession",
            Message   = "No active session to clone."
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.False(cts.IsCancellationRequested);
    }

    // ── session.getMessages result path ──────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_NoActiveSession_CommandErrorEvent_RendersFailedLine()
    {
        // session.getMessages failure is CommandErrorEvent — verify the host renders it.
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        CommandErrorEvent evt = new()
        {
            CommandId = "req-msg",
            Command   = "session.getMessages",
            Code      = "noActiveSession",
            Message   = "No active session."
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        IEnumerable<string> lines = fake.Calls
            .OfType<ScrollbackAppendLine>()
            .Select(c => LineText(c.Line));

        Assert.Contains(lines, l =>
            l.Contains("[Failed]") &&
            l.Contains("session.getMessages"));
    }

    [Fact]
    public async Task SessionMessagesResultEvent_DoesNotSetActiveSessionId()
    {
        // SessionMessagesResultEvent is a history fetch — it must not affect session tracking.
        FakeTerminal fake = new();
        List<Command> cmds = [];
        using CancellationTokenSource cts = new();
        ConsoleEventHandler handler = BuildHandler(fake, cmds, cts);

        SessionMessagesResultEvent evt = new()
        {
            CommandId = "req-msg",
            Messages  = []
        };

        await handler.HandleRpcEventAsync((Event)evt, CancellationToken.None);

        Assert.Null(handler.ActiveSessionId);
    }
}
