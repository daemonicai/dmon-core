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

        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
    public void TurnEnd_SettlesStreamingMessage_WithFinalRecord()
    {
        // Arrange
        FakeCoreSession session = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

        // Push two deltas.
        session.Push(MakeDeltaEvent("hello "));
        session.Push(MakeDeltaEvent("world"));

        // Advance the scheduler to flush the coalescing buffer.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Act — fire TurnEndEvent with a final MessageRecord containing a TextPart.
        MessageRecord settled = new()
        {
            EntryId = "msg-1",
            Timestamp = DateTimeOffset.UtcNow,
            Role = "assistant",
            Parts = [new TextPart { Text = "hello world" }]
        };
        session.Push(MakeTurnEndEvent(settled));

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

        session.Push(MakeDeltaEvent("hello "));
        session.Push(MakeDeltaEvent("world"));
        Assert.Empty(sut.Messages);

        MessageRecord settled = new()
        {
            EntryId  = "msg-race",
            Timestamp = DateTimeOffset.UtcNow,
            Role     = "assistant",
            Parts    = [new TextPart { Text = "hello world" }]
        };
        session.Push(MakeTurnEndEvent(settled));

        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

        session.Push(MakeDeltaEvent("hello "));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);
        Assert.Single(sut.Messages);

        session.Push(MakeDeltaEvent("world"));

        MessageRecord settled = new()
        {
            EntryId   = "msg-race2",
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     = [new TextPart { Text = "hello world" }]
        };
        session.Push(MakeTurnEndEvent(settled));
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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

        session.Push(MakeDeltaEvent("turn1 delta"));

        MessageRecord turn1Settled = new()
        {
            EntryId   = "msg-turn1",
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     = [new TextPart { Text = "turn1 settled" }]
        };
        session.Push(MakeTurnEndEvent(turn1Settled));
        session.Push(new TurnStartEvent());

        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("turn1 settled", part.Text);
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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
        ConversationViewModel sut = new(fakeScreen, session, scheduler);

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
        session.Push(new TurnEndEvent { Message = new object(), ToolResults = [] });
        scheduler.AdvanceBy(1);

        bool? afterStream = null;
        sut.SendPrompt.CanExecute.Subscribe(v => afterStream = v);
        scheduler.AdvanceBy(1);
        Assert.True(afterStream);
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

    private static TurnEndEvent MakeTurnEndEvent(MessageRecord record)
    {
        JsonElement messageElement = JsonSerializer.SerializeToElement(record, WireSerializerOptions.Default);
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
