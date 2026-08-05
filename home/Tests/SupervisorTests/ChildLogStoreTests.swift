import Foundation
import Testing
@testable import Supervisor

@Suite
struct ChildLogStoreTests {
    @Test
    func aChildNeverObservedReportsAnEmptyBuffer() async {
        let store = ChildLogStore()
        let buffer = await store.buffer(for: "network-gateway")
        #expect(buffer.lines.isEmpty)
        #expect(buffer.droppedCount == 0)
    }

    @Test
    func appendRecordsALineWithItsSourceAndTextForThatChildOnly() async {
        let store = ChildLogStore()
        await store.append("hello", source: .standardOutput, for: "network-gateway")

        let buffer = await store.buffer(for: "network-gateway")
        #expect(buffer.lines.map(\.text) == ["hello"])
        #expect(buffer.lines.first?.source == .standardOutput)
        #expect(buffer.lines.first?.childID == "network-gateway")
        #expect(await store.buffer(for: "dcal").lines.isEmpty)
    }

    @Test
    func appendsForDifferentChildrenAreKeptSeparate() async {
        let store = ChildLogStore()
        await store.append("from-a", source: .standardOutput, for: "child-a")
        await store.append("from-b", source: .standardError, for: "child-b")

        #expect(await store.buffer(for: "child-a").lines.map(\.text) == ["from-a"])
        #expect(await store.buffer(for: "child-b").lines.map(\.text) == ["from-b"])
    }

    /// "The buffer is bounded and the bound is visible" (Architect call 3):
    /// the oldest lines are dropped first, and `droppedCount` records
    /// exactly how many — a capped, chatty child must stay distinguishable
    /// from one that genuinely stayed quiet, not look identical to it.
    @Test
    func exceedingCapacityDropsTheOldestLinesAndReportsTheDropCount() async {
        let store = ChildLogStore(capacityPerChild: 3)
        for i in 1...5 {
            await store.append("line-\(i)", source: .standardOutput, for: "network-gateway")
        }

        let buffer = await store.buffer(for: "network-gateway")
        #expect(buffer.lines.map(\.text) == ["line-3", "line-4", "line-5"], "the three most recent lines should survive")
        #expect(buffer.droppedCount == 2, "the two oldest lines should be counted as dropped")
    }

    /// A no-op-looking append (capacity already exceeded, so the visible
    /// window doesn't change) must still keep incrementing the drop count —
    /// proven separately from the first overflow, since a store that only
    /// computed the drop count once at the moment of first overflow would
    /// still pass the test above.
    @Test
    func droppedCountKeepsAccumulatingAcrossRepeatedOverflow() async {
        let store = ChildLogStore(capacityPerChild: 2)
        for i in 1...6 {
            await store.append("line-\(i)", source: .standardOutput, for: "network-gateway")
        }

        let buffer = await store.buffer(for: "network-gateway")
        #expect(buffer.lines.map(\.text) == ["line-5", "line-6"])
        #expect(buffer.droppedCount == 4)
    }

    @Test
    func subscribersObserveEachAppendedChange() async {
        let store = ChildLogStore()
        var iterator = await store.updates().makeAsyncIterator()

        let initial = await iterator.next()
        #expect(initial == [:])

        await store.append("hello", source: .standardOutput, for: "network-gateway")
        let afterAppend = await iterator.next()
        #expect(afterAppend?["network-gateway"]?.lines.map(\.text) == ["hello"])
    }

    /// Fan-out, not just delivery to whichever subscriber happens to be
    /// asked about: two independent subscriptions must each see every
    /// append.
    @Test
    func multipleSubscribersEachReceiveTheFullFanOut() async {
        let store = ChildLogStore()
        var first = await store.updates().makeAsyncIterator()
        var second = await store.updates().makeAsyncIterator()
        _ = await first.next()
        _ = await second.next()

        await store.append("hello", source: .standardOutput, for: "network-gateway")

        #expect(await first.next()?["network-gateway"]?.lines.map(\.text) == ["hello"])
        #expect(await second.next()?["network-gateway"]?.lines.map(\.text) == ["hello"])
    }

    /// `onTermination` must actually unregister — otherwise a subscriber
    /// that stops iterating leaks its continuation (and an `.unbounded`
    /// buffer) forever rather than being forgotten.
    @Test
    func endingIterationUnregistersTheSubscriber() async {
        let store = ChildLogStore()

        do {
            let stream = await store.updates()
            var iterator = stream.makeAsyncIterator()
            _ = await iterator.next()
            #expect(await store.subscriberCountForTesting == 1)
        }

        let unregistered = await waitUntilTrue(timeout: 2) {
            await store.subscriberCountForTesting == 0
        }
        #expect(unregistered)
    }
}
