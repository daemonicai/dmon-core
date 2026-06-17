using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive.Subjects;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Microsoft.Reactive.Testing;
using ReactiveUI;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Group 5 — conversation rendering tests.
///
/// 5.4: Proves that a burst of <c>textDelta</c> events is coalesced into O(1) UI updates
///      per window, not N, and that the settled text after the window equals the
///      concatenation of all deltas.
///
/// 5.5: Proves that <see cref="UnknownPartViewModel"/> is present in the rendered VM but
///      never included in any outbound payload.
/// </summary>
public sealed class ConversationViewModelTests
{
    // =========================================================================
    // 5.4 — coalescing: burst of N deltas collapses to bounded UI updates
    // =========================================================================

    [Fact]
    public void DeltaBurst_CoalesceWindow_CollapsesBatchToSingleAppend()
    {
        // Arrange
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();

        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // Track how many times the Messages collection changes.
        int collectionChangeCount = 0;
        ((System.Collections.Specialized.INotifyCollectionChanged)sut.Messages)
            .CollectionChanged += (_, _) => collectionChangeCount++;

        // Build N textDelta events.
        const int n = 20;
        string[] deltas = Enumerable.Range(1, n).Select(i => $"word{i} ").ToArray();
        string expected = string.Concat(deltas);

        // Act — push all N deltas into the subject BEFORE advancing the scheduler.
        foreach (string delta in deltas)
        {
            events.OnNext(MakeDeltaEvent(delta));
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
        // One message was added (1 change) plus at most a handful of part updates,
        // certainly not N times.
        Assert.True(collectionChangeCount < n,
            $"Expected fewer than {n} collection changes but got {collectionChangeCount}. " +
            "Coalescing is not working — each delta is triggering a separate UI update.");
    }

    [Fact]
    public void TurnEnd_SettlesStreamingMessage_WithFinalRecord()
    {
        // Arrange
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // Push two deltas.
        events.OnNext(MakeDeltaEvent("hello "));
        events.OnNext(MakeDeltaEvent("world"));

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
        events.OnNext(MakeTurnEndEvent(settled));

        // Assert: exactly one message, its part reflects the settled text.
        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    [Fact]
    public void MultipleBursts_EachInSeparateWindow_ProducesMultipleAppends()
    {
        // Proves each window tick produces a bounded batch, not one giant concatenation
        // across windows.
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // First burst.
        events.OnNext(MakeDeltaEvent("A"));
        events.OnNext(MakeDeltaEvent("B"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Second burst.
        events.OnNext(MakeDeltaEvent("C"));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        // The two batches should be appended in order: "AB" then "C".
        Assert.Equal("ABC", part.Text);
    }

    // =========================================================================
    // 5.5 — UnknownPart is render-only: present in VM but excluded from outbound
    // =========================================================================

    [Fact]
    public void UnknownPart_IsPresentInRenderedMessage_ButExcludedFromOutboundParts()
    {
        // Arrange — build a MessageRecord with a mix of known and unknown parts.
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

        // Assert part 1: all three parts are present in the rendered VM.
        Assert.Equal(3, vm.Parts.Count);
        Assert.IsType<TextPartViewModel>(vm.Parts[0]);
        Assert.IsType<UnknownPartViewModel>(vm.Parts[1]);
        Assert.IsType<TextPartViewModel>(vm.Parts[2]);

        // Assert part 2: OutboundParts() excludes the UnknownPartViewModel.
        IReadOnlyList<PartViewModel> outbound = vm.OutboundParts();
        Assert.Equal(2, outbound.Count);
        Assert.All(outbound, p => Assert.IsNotType<UnknownPartViewModel>(p));
    }

    [Fact]
    public void UnknownPart_DisplayLabel_IncludesRawJsonAndProducer()
    {
        // Confirms UnknownPartViewModel presents useful render-only information.
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
        // UsagePart is also render-only metadata; must not appear in outbound payloads.
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
    // Settle/flush ordering race — regression test
    // =========================================================================

    /// <summary>
    /// Regression: when TurnEndEvent fires while buffered deltas are still pending (before
    /// the coalescing window advances), SettleTurn must win and the trailing buffer flush
    /// must NOT create an orphan/duplicate assistant bubble.
    ///
    /// Production ordering on RxApp.MainThreadScheduler:
    ///   1. Deltas D1..Dn enter the 50ms buffer (not yet flushed).
    ///   2. TurnEndEvent fires immediately → SettleTurn settles the in-progress assistant.
    ///   3. Scheduler advances past the window → buffer flushes stale deltas.
    ///
    /// Expected: exactly ONE assistant message whose text matches the authoritative
    /// MessageRecord from TurnEndEvent, not a duplicate trailing bubble.
    /// </summary>
    [Fact]
    public void TurnEnd_BeforeBufferFlush_DoesNotProduceOrphanBubble()
    {
        // Arrange
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // Push deltas into the subject — they are now buffered but NOT yet flushed
        // (the TestScheduler has not advanced past the coalescing window).
        events.OnNext(MakeDeltaEvent("hello "));
        events.OnNext(MakeDeltaEvent("world"));

        // Confirm nothing is visible yet (buffer hasn't flushed).
        Assert.Empty(sut.Messages);

        // Act step 1 — fire TurnEndEvent BEFORE advancing the scheduler.
        // This is the race: settle arrives while deltas are still buffered.
        MessageRecord settled = new()
        {
            EntryId  = "msg-race",
            Timestamp = DateTimeOffset.UtcNow,
            Role     = "assistant",
            Parts    = [new TextPart { Text = "hello world" }]
        };
        events.OnNext(MakeTurnEndEvent(settled));

        // The scheduler still hasn't advanced — TurnEnd was processed synchronously
        // (no buffer delay on TurnEndEvent), so SettleTurn has run.
        // Because no in-progress assistant existed yet (buffer not flushed), SettleTurn
        // takes the else-branch: appends from the authoritative record.
        // _currentGeneration is now 1; the buffered deltas carry generation 0.

        // Act step 2 — advance past the coalescing window so the buffer flushes.
        // The stale deltas must be silently dropped, not resurrected as a new bubble.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Assert: exactly ONE message, no orphan.
        Assert.Single(sut.Messages);

        // Its content must be the authoritative settled text, not the raw streaming delta.
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    /// <summary>
    /// Variant: deltas DID create an in-progress assistant (buffer flushed at least once),
    /// then TurnEndEvent arrives and settles it, then MORE buffered deltas arrive.
    /// The second flush must also be dropped — no second bubble or mutation of settled text.
    /// </summary>
    [Fact]
    public void TurnEnd_AfterFirstFlush_SecondFlushIsDropped()
    {
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // First flush: push deltas and advance scheduler — creates in-progress assistant.
        events.OnNext(MakeDeltaEvent("hello "));
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        Assert.Single(sut.Messages);

        // Push more deltas (still buffered, not yet flushed).
        events.OnNext(MakeDeltaEvent("world"));

        // TurnEndEvent arrives before the second flush.
        MessageRecord settled = new()
        {
            EntryId   = "msg-race2",
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     = [new TextPart { Text = "hello world" }]
        };
        events.OnNext(MakeTurnEndEvent(settled));

        // Advance past the window — second flush fires, must be dropped.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Still exactly one message — no orphan bubble.
        Assert.Single(sut.Messages);

        // Text is the settled authoritative content.
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("hello world", part.Text);
    }

    /// <summary>
    /// Cross-turn race regression: turn-1 deltas are buffered, TurnEndEvent fires,
    /// then TurnStartEvent fires (turn 2 begins) — all before the coalescing window
    /// has elapsed. When the window flushes, the stale turn-1 deltas must NOT be
    /// appended to the turn-2 stream or create an orphan bubble.
    ///
    /// This was the failure mode of the prior single-bool guard: TurnStart reset the
    /// bool to false, so the stale flush saw a clear guard and created a duplicate bubble.
    /// The generation counter is immune because the tag is captured at emission and is
    /// never reset — only SettleTurn increments the generation.
    /// </summary>
    [Fact]
    public void CrossTurnRace_StaleDeltas_DroppedAfterTurnStartResets()
    {
        Subject<Event> events = new();
        TestScheduler scheduler = new();
        IScreen fakeScreen = new FakeScreen();
        ConversationViewModel sut = new(fakeScreen, events, scheduler);

        // Turn 1: push deltas (buffered, not yet flushed).
        events.OnNext(MakeDeltaEvent("turn1 delta"));

        // Turn 1 ends: SettleTurn fires, generation increments to 1.
        MessageRecord turn1Settled = new()
        {
            EntryId   = "msg-turn1",
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     = [new TextPart { Text = "turn1 settled" }]
        };
        events.OnNext(MakeTurnEndEvent(turn1Settled));

        // Turn 2 begins immediately (still within the 50ms window for turn-1 deltas).
        events.OnNext(new TurnStartEvent());

        // Advance past the coalescing window — the turn-1 buffered delta flushes here.
        // With the prior bool guard, TurnStart would have reset the guard to false, so
        // EnsureInProgressAssistant would run and create an orphan bubble for turn-1 text.
        // With the generation counter, the batch carries generation 0; _currentGeneration
        // is 1 (bumped by SettleTurn); the batch is silently dropped.
        scheduler.AdvanceBy(ConversationViewModel.DeltaCoalesceWindow.Ticks + 1);

        // Assert: exactly ONE message — the turn-1 settled record (from the else branch
        // in SettleTurn, since no in-progress assistant existed when TurnEnd fired).
        // No orphan bubble containing "turn1 delta" appended after TurnStart.
        Assert.Single(sut.Messages);
        TextPartViewModel part = Assert.IsType<TextPartViewModel>(sut.Messages[0].Parts[0]);
        Assert.Equal("turn1 settled", part.Text);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static MessageDeltaEvent MakeDeltaEvent(string text)
    {
        // Construct the delta payload as a JsonElement (the shape the RPC layer emits).
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
