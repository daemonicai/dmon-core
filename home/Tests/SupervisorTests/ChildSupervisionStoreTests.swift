import Foundation
import Testing
@testable import Supervisor

@Suite
struct ChildSupervisionStoreTests {
    @Test
    func aChildNeverObservedReportsNormal() async {
        let store = ChildSupervisionStore()
        let state = await store.state(for: "network-gateway")
        #expect(state == .normal)
    }

    @Test
    func publishRecordsStateForThatChildOnly() async {
        let store = ChildSupervisionStore()
        await store.publish(.restarting(delay: 2), for: "network-gateway")

        #expect(await store.state(for: "network-gateway") == .restarting(delay: 2))
        #expect(await store.state(for: "dcal") == .normal)
        #expect(await store.currentSnapshot() == ["network-gateway": .restarting(delay: 2)])
    }

    @Test
    func laterPublishesOverwritePriorStateForTheSameChild() async {
        let store = ChildSupervisionStore()
        await store.publish(.restarting(delay: 2), for: "network-gateway")
        await store.publish(.stoppedIntentionally, for: "network-gateway")
        #expect(await store.state(for: "network-gateway") == .stoppedIntentionally)
    }

    /// "Health is visible": the store's own publication, independent of any
    /// UI. A subscriber sees the snapshot at subscription time, then again
    /// after each `publish`.
    @Test
    func subscribersObserveEachPublishedChange() async {
        let store = ChildSupervisionStore()
        var iterator = await store.updates().makeAsyncIterator()

        let initial = await iterator.next()
        #expect(initial == [:])

        await store.publish(.restarting(delay: 2), for: "network-gateway")
        let afterFirstPublish = await iterator.next()
        #expect(afterFirstPublish == ["network-gateway": .restarting(delay: 2)])

        await store.publish(.repeatedFailure(delay: 60), for: "dcal")
        let afterSecondPublish = await iterator.next()
        #expect(afterSecondPublish == ["network-gateway": .restarting(delay: 2), "dcal": .repeatedFailure(delay: 60)])
    }

    /// The store's own documented claim, asserted directly rather than
    /// incidentally: publishing the *same* state again still notifies —
    /// a subscriber that deduplicated on its own would still see two
    /// notifications here, but a store that deduplicated internally would
    /// only ever produce the first.
    @Test
    func aNoOpPublishStillNotifiesSubscribers() async {
        let store = ChildSupervisionStore()
        var iterator = await store.updates().makeAsyncIterator()
        _ = await iterator.next() // the initial, empty snapshot

        await store.publish(.restarting(delay: 2), for: "network-gateway")
        let first = await iterator.next()

        await store.publish(.restarting(delay: 2), for: "network-gateway")
        let second = await iterator.next()

        #expect(first == ["network-gateway": .restarting(delay: 2)])
        #expect(second == ["network-gateway": .restarting(delay: 2)])
    }

    /// Fan-out, not just delivery to whichever subscriber happens to be
    /// asked about: two independent subscriptions must each see every
    /// publish, not share one continuation or only reach the first
    /// subscriber registered.
    @Test
    func multipleSubscribersEachReceiveTheFullFanOut() async {
        let store = ChildSupervisionStore()
        var first = await store.updates().makeAsyncIterator()
        var second = await store.updates().makeAsyncIterator()
        _ = await first.next()
        _ = await second.next()

        await store.publish(.repeatedFailure(delay: 60), for: "network-gateway")

        #expect(await first.next() == ["network-gateway": .repeatedFailure(delay: 60)])
        #expect(await second.next() == ["network-gateway": .repeatedFailure(delay: 60)])
    }

    /// `onTermination` must actually unregister — otherwise a subscriber
    /// that stops iterating leaks its continuation (and an `.unbounded`
    /// buffer) forever rather than being forgotten.
    @Test
    func endingIterationUnregistersTheSubscriber() async {
        let store = ChildSupervisionStore()

        do {
            let stream = await store.updates()
            var iterator = stream.makeAsyncIterator()
            _ = await iterator.next()
            #expect(await store.subscriberCountForTesting == 1)
        }
        // `stream` and `iterator` are now out of scope and deinitialised —
        // there is no explicit "unsubscribe" call to make; dropping every
        // reference is what drives `AsyncStream` to run `onTermination`.

        let unregistered = await waitUntilTrue(timeout: 2) {
            await store.subscriberCountForTesting == 0
        }
        #expect(unregistered)
    }
}
