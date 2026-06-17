using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive.Subjects;
using System.Text.Json;
using Dmon.Desktop;
using Dmon.Protocol;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;
using ReactiveUI;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Group 5 — conversation rendering tests.
/// Group 6.1 — SendPrompt CanExecute/IsStreaming tests.
///
/// 5.4: Proves that a burst of <c>textDelta</c> events is coalesced into O(1) UI updates
///      per window, not N, and that the settled text after the window equals the
///      concatenation of all deltas.
///
/// 5.5: Proves that <see cref="UnknownPartViewModel"/> is present in the rendered VM but
///      never included in any outbound payload.
/// </summary>
public sealed class ConversationViewModelTests : IClassFixture<ReactiveUiTestFixture>
{
    // =========================================================================
    // 5.4 — coalescing: burst of N deltas collapses to bounded UI updates
    // =========================================================================

    [Fact]
    public void DeltaBurst_CoalesceWindow_CollapsesBatchToSingleAppend()
    {
        // Arrange
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();

        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        // Track how many times the Messages collection changes.
        int collectionChangeCount = 0;
        ((INotifyCollectionChanged)sut.Messages)
            .CollectionChanged += (_, _) => collectionChangeCount++;

        // Build N textDelta events.
        const int n = 20;
        string[] deltas = Enumerable.Range(1, n).Select(i => $"word{i} ").ToArray();
        string expected = string.Concat(deltas);

        // Act — push all N deltas into the subject BEFORE advancing the scheduler.
        foreach (string delta in deltas)
        {
            session.Push(MakeDeltaEvent(delta));
        }

        // Assert part 1: before the scheduler advances, nothing has been applied yet.
        Assert.Empty(sut.Messages);

        // Act — advance the scheduler by more than the coalescing window so the buffer flushes.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Assert part 2: exactly ONE message was added to the list (the in-progress assistant).
        Assert.Single(sut.Messages);

        // Assert part 3: the message has one TextPartViewModel whose text equals the
        // concatenation of all deltas (the whole burst was coalesced into one batch).
        TextPartViewModel textPart = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal(expected, textPart.Text);

        // Assert part 4 (coalescing proof): the collection changed FAR fewer times than N.
        Assert.True(collectionChangeCount < n,
            $"Expected fewer than {n} collection changes but got {collectionChangeCount}. " +
            "Coalescing is not working — each delta is triggering a separate UI update.");
    }

    [Fact]
    public void TurnEnd_SettlesStreamingMessage_WithFinalContent()
    {
        // Arrange
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        // Push two deltas.
        session.Push(MakeDeltaEvent("hello "));
        session.Push(MakeDeltaEvent("world"));

        // Advance the scheduler to flush the coalescing buffer.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Act — fire TurnEndEvent with the real wire shape: { role, content }.
        session.Push(MakeTurnEndEvent("hello world"));

        // Assert: exactly one message, its part reflects the settled text.
        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    [Fact]
    public void MultipleBursts_EachInSeparateWindow_ProducesMultipleAppends()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        // First burst.
        session.Push(MakeDeltaEvent("A"));
        session.Push(MakeDeltaEvent("B"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Second burst.
        session.Push(MakeDeltaEvent("C"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("ABC", part.Text);
    }

    // =========================================================================
    // 5.5 — UnknownPart is render-only: present in VM but excluded from outbound
    // =========================================================================

    [Fact]
    public void UnknownPart_IsPresentInRenderedMessage_ButExcludedFromOutboundParts()
    {
        MessageRecord record = new()
        {
            EntryId = "msg-u1",
            Timestamp = DateTimeOffset.UtcNow,
            Role = "assistant",
            Parts =
            [
                new TextPart { Text = "visible text" },
                new UnknownPart { Raw = JsonDocument.Parse("{\"x\":1}").RootElement, ProducedBy = "test-provider" },
                new TextPart { Text = "more text" },
            ]
        };

        MessageViewModel vm = new(record);

        Assert.Equal(3, vm.Parts.Count);
        Assert.IsType<TextPartViewModel>(vm.Parts[0]);
        Assert.IsType<UnknownPartViewModel>(vm.Parts[1]);
        Assert.IsType<TextPartViewModel>(vm.Parts[2]);

        IReadOnlyList<PartViewModel> outbound = vm.OutboundParts();
        Assert.Equal(2, outbound.Count);
        Assert.All(outbound, p => Assert.IsNotType<UnknownPartViewModel>(p));
    }

    [Fact]
    public void UnknownPart_DisplayLabel_IncludesRawJsonAndProducer()
    {
        UnknownPart part = new()
        {
            Raw = JsonDocument.Parse("{\"exotic\":true}").RootElement,
            ProducedBy = "exotic-provider"
        };
        UnknownPartViewModel vm = new(part);

        Assert.Contains("exotic-provider", vm.DisplayLabel);
        Assert.Contains("[unknown:exotic-provider]", vm.DisplayLabel);
    }

    [Fact]
    public void UsagePart_IsExcludedFromOutboundParts()
    {
        MessageRecord record = new()
        {
            EntryId = "msg-u2",
            Timestamp = DateTimeOffset.UtcNow,
            Role = "assistant",
            Parts =
            [
                new TextPart { Text = "answer" },
                new UsagePart { InputTokens = 10, OutputTokens = 5 },
            ]
        };

        MessageViewModel vm = new(record);

        Assert.Equal(2, vm.Parts.Count);
        Assert.IsType<TextPartViewModel>(vm.Parts[0]);
        Assert.IsType<UsagePartViewModel>(vm.Parts[1]);

        IReadOnlyList<PartViewModel> outbound = vm.OutboundParts();
        Assert.Single(outbound);
        Assert.IsType<TextPartViewModel>(outbound[0]);
    }

    // =========================================================================
    // Settle/flush ordering race — regression tests
    // =========================================================================

    [Fact]
    public void TurnEnd_BeforeBufferFlush_DoesNotProduceOrphanBubble()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(MakeDeltaEvent("hello "));
        session.Push(MakeDeltaEvent("world"));
        Assert.Empty(sut.Messages);

        // TurnEnd fires before the coalescing window elapses.
        session.Push(MakeTurnEndEvent("hello world"));

        // Now advance the scheduler: the buffered deltas arrive AFTER settle and are dropped.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Exactly one message, settled from TurnEnd's content (not duplicated by dropped deltas).
        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    [Fact]
    public void TurnEnd_AfterFirstFlush_SecondFlushIsDropped()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(MakeDeltaEvent("hello "));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);
        Assert.Single(sut.Messages);

        session.Push(MakeDeltaEvent("world"));

        // TurnEnd settles with the authoritative full text.
        session.Push(MakeTurnEndEvent("hello world"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    [Fact]
    public void CrossTurnRace_StaleDeltas_DroppedAfterTurnStartResets()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(MakeDeltaEvent("turn1 delta"));

        session.Push(MakeTurnEndEvent("turn1 settled"));
        session.Push(new TurnStartEvent());

        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("turn1 settled", part.Text);
    }

    // =========================================================================
    // Fast-turn: deltas buffered, TurnEnd fires before window elapses, then
    // the buffer flushes and must be dropped — settle is the sole renderer.
    //
    // This is the live failure: short turns arrive so fast that the entire turn
    // (deltas + TurnEnd) lands within one 50ms coalescing window. TurnEnd fires
    // (incrementing the generation), then the buffer flushes with the now-stale
    // generation and is dropped. SettleTurn must be the ONLY thing that renders.
    // =========================================================================

    [Fact]
    public void FastTurn_TurnEndBeforeBufferFlush_AssistantMessageRendersFromContent()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        // Push deltas — these are buffered and NOT yet applied (no scheduler advance).
        session.Push(MakeDeltaEvent("fast "));
        session.Push(MakeDeltaEvent("answer"));

        // Before advancing the scheduler (no flush yet), TurnEnd fires.
        // This increments the generation, making the buffered deltas stale.
        session.Push(MakeTurnEndEvent("fast answer"));

        // The VM must already have a message (SettleTurn created it from content).
        Assert.Single(sut.Messages);
        TextPartViewModel partBeforeFlush = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("fast answer", partBeforeFlush.Text);

        // Now advance the scheduler so the buffered delta batch flushes.
        // The batch carries the old generation and must be DROPPED.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Still exactly one message, text unchanged (deltas did not duplicate or overwrite).
        Assert.Single(sut.Messages);
        TextPartViewModel partAfterFlush = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("fast answer", partAfterFlush.Text);
    }

    // =========================================================================
    // Two-scheduler path — regression guard for the Heisenbug where Buffer's window
    // never fires when coalesceScheduler IS the live UI dispatcher
    // =========================================================================

    /// <summary>
    /// Exercises the split-scheduler path: coalesceScheduler and uiScheduler are
    /// two distinct <see cref="TestScheduler"/> instances. A burst of deltas should
    /// only appear in the VM after BOTH (a) the coalesce window elapses on the coalesce
    /// scheduler AND (b) the ui scheduler is advanced to pump the ObserveOn marshal.
    ///
    /// This is the headless proxy for the live Heisenbug: if the coalesce window timer
    /// were run on the UI scheduler, the flush would not arrive until the UI scheduler
    /// was advanced — which in production means "never, because the dispatcher is busy".
    /// </summary>
    [Fact]
    public void TwoSchedulers_DeltaAppearsOnlyAfterBothSchedulersAdvance()
    {
        FakeCoreSession session = new();
        TestScheduler coalesceScheduler = new();
        TestScheduler uiScheduler = new();
        IScreen fakeScreen = new FakeScreen();

        ConversationViewModel sut = new(fakeScreen, session, uiScheduler, coalesceScheduler);

        session.Push(MakeDeltaEvent("hello "));
        session.Push(MakeDeltaEvent("world"));

        // Part 1: advancing ONLY the coalesce scheduler past the window flushes the batch
        // off the thread-pool timer but the marshal onto the UI thread has not yet occurred.
        // The VM must still be empty (ObserveOn has queued work on the uiScheduler).
        coalesceScheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);
        Assert.Empty(sut.Messages);

        // Part 2: advancing the uiScheduler pumps the queued ObserveOn work.
        // Now the message must appear.
        uiScheduler.AdvanceBy(1);
        Assert.Single(sut.Messages);

        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    // =========================================================================
    // 6.1 — SendPrompt CanExecute / IsStreaming
    // =========================================================================

    [Fact]
    public void SendPrompt_CanExecute_FalseWhileStreaming()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "hello";

        // Advance scheduler to materialise initial IsStreaming = false.
        scheduler.AdvanceBy(1);

        bool? canExecuteBeforeStream = null;
        sut.SendPrompt.CanExecute.Subscribe(v => canExecuteBeforeStream = v);
        scheduler.AdvanceBy(1);

        Assert.True(canExecuteBeforeStream);

        // Start streaming.
        session.Push(new TurnStartEvent());
        scheduler.AdvanceBy(1);

        bool? canExecuteWhileStreaming = null;
        sut.SendPrompt.CanExecute.Subscribe(v => canExecuteWhileStreaming = v);
        scheduler.AdvanceBy(1);

        Assert.False(canExecuteWhileStreaming);
    }

    [Fact]
    public void SendPrompt_CanExecute_TrueWhenIdleAndNonBlankPrompt()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "  "; // whitespace only — should be false
        scheduler.AdvanceBy(1);

        bool? canExecuteBlank = null;
        sut.SendPrompt.CanExecute.Subscribe(v => canExecuteBlank = v);
        scheduler.AdvanceBy(1);

        Assert.False(canExecuteBlank);

        sut.Prompt = "hello world";
        scheduler.AdvanceBy(1);

        bool? canExecuteWithText = null;
        sut.SendPrompt.CanExecute.Subscribe(v => canExecuteWithText = v);
        scheduler.AdvanceBy(1);

        Assert.True(canExecuteWithText);
    }

    [Fact]
    public void SendPrompt_Execute_SendsTurnSubmitCommandAndClearsPrompt()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "what is the answer?";
        scheduler.AdvanceBy(1);

        // Execute synchronously by subscribing then advancing.
        sut.SendPrompt.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        // Assert: a TurnSubmitCommand was sent.
        Assert.Single(session.SentCommands);
        TurnSubmitCommand cmd = Assert.IsType<TurnSubmitCommand>(session.SentCommands[0]);
        Assert.Equal("what is the answer?", cmd.Message);

        // Assert: Prompt was cleared.
        Assert.Equal(string.Empty, sut.Prompt);
    }

    [Fact]
    public void SendPrompt_ReEnabled_AfterTurnEnd()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "hello";
        scheduler.AdvanceBy(1);

        // Start streaming → CanExecute false.
        session.Push(new TurnStartEvent());
        scheduler.AdvanceBy(1);

        bool? midStream = null;
        sut.SendPrompt.CanExecute.Subscribe(v => midStream = v);
        scheduler.AdvanceBy(1);
        Assert.False(midStream);

        // End turn → CanExecute true (prompt still has text).
        // Use the real wire shape; content is empty string (no text) — that's fine for this test.
        session.Push(MakeTurnEndEvent(string.Empty));
        scheduler.AdvanceBy(1);

        bool? afterStream = null;
        sut.SendPrompt.CanExecute.Subscribe(v => afterStream = v);
        scheduler.AdvanceBy(1);
        Assert.True(afterStream);
    }

    // =========================================================================
    // Gap 1 — Echo user message on SendPrompt
    // =========================================================================

    [Fact]
    public void SendPrompt_Execute_EchoesUserMessage()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "hello";
        scheduler.AdvanceBy(1);

        sut.SendPrompt.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        // A user message must appear in the list.
        Assert.Contains(sut.Messages, m => m.Role == "user");
        MessageViewModel userMsg = sut.Messages.Single(m => m.Role == "user");
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(userMsg.Parts[0]);
        Assert.Equal("hello", part.Text);

        // The TurnSubmitCommand was also sent (existing behaviour preserved).
        Assert.Single(session.SentCommands);
    }

    [Fact]
    public void SendPrompt_Execute_UserEchoDoesNotDuplicateOnTurnEnd()
    {
        // TurnEndEvent carries the ASSISTANT record, not the user turn.
        // After TurnEnd the user echo must still be present exactly once.
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        sut.Prompt = "ping";
        scheduler.AdvanceBy(1);

        sut.SendPrompt.Execute().Subscribe();
        scheduler.AdvanceBy(1);

        // Simulate a TurnStart + delta + TurnEnd (assistant responds).
        session.Push(new TurnStartEvent());
        session.Push(MakeDeltaEvent("pong"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        session.Push(MakeTurnEndEvent("pong"));
        scheduler.AdvanceBy(1);

        // Exactly one "user" message, exactly one "assistant" message.
        Assert.Single(sut.Messages.Where(m => m.Role == "user"));
        Assert.Single(sut.Messages.Where(m => m.Role == "assistant"));
    }

    // =========================================================================
    // Gap 2 — Surface core status events as system messages
    // =========================================================================

    [Fact]
    public void ErrorEvent_AddsSystemMessage()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(new ErrorEvent { Code = "someError", Message = "something went wrong", Recoverable = false });
        scheduler.AdvanceBy(1);

        MessageViewModel msg = Assert.Single(sut.Messages.Where(m => m.Role == "system"));
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(msg.Parts[0]);
        Assert.Contains("[Error]", part.Text);
        Assert.Contains("something went wrong", part.Text);
    }

    [Fact]
    public void SetupRequiredEvent_AddsActionableSystemMessage()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(new SetupRequiredEvent { Adapters = [] });
        scheduler.AdvanceBy(1);

        MessageViewModel msg = Assert.Single(sut.Messages.Where(m => m.Role == "system"));
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(msg.Parts[0]);
        Assert.Contains("[Setup required]", part.Text);
        Assert.Contains("No provider configured", part.Text);
    }

    [Fact]
    public void CommandErrorEvent_AddsSystemMessage()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(new CommandErrorEvent { CommandId = "cmd-1", Command = "session.create", Code = "noSession", Message = "session not found" });
        scheduler.AdvanceBy(1);

        MessageViewModel msg = Assert.Single(sut.Messages.Where(m => m.Role == "system"));
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(msg.Parts[0]);
        Assert.Contains("[Failed]", part.Text);
        Assert.Contains("session.create", part.Text);
        Assert.Contains("session not found", part.Text);
    }

    [Fact]
    public void SystemNoticeEvent_AddsSystemMessage()
    {
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler, scheduler);

        session.Push(new SystemNoticeEvent { Message = "compaction running" });
        scheduler.AdvanceBy(1);

        MessageViewModel msg = Assert.Single(sut.Messages.Where(m => m.Role == "system"));
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(msg.Parts[0]);
        Assert.Contains("[Notice]", part.Text);
        Assert.Contains("compaction running", part.Text);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static MessageDeltaEvent MakeDeltaEvent(string text)
    {
        JsonElement delta = JsonSerializer.SerializeToElement(
            new { type = "textDelta", delta = text },
            WireSerializerOptions.Default);

        return new MessageDeltaEvent
        {
            Message = JsonDocument.Parse("{}").RootElement,
            Delta = delta
        };
    }

    /// <summary>
    /// Builds a <see cref="TurnEndEvent"/> whose Message carries the REAL wire shape:
    /// <c>{ "role": "assistant", "content": "..." }</c>. This is what the core actually
    /// emits — NOT a parts-based MessageRecord.
    /// </summary>
    private static TurnEndEvent MakeTurnEndEvent(string content)
    {
        JsonElement messageElement = JsonSerializer.SerializeToElement(
            new { role = "assistant", content },
            WireSerializerOptions.Default);

        return new TurnEndEvent
        {
            Message = messageElement,
            ToolResults = []
        };
    }

    private sealed class FakeScreen : ReactiveObject, IScreen
    {
        public RoutingState Router { get; } = new();
    }
}
