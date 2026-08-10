import Testing
@testable import Supervisor

@Suite
struct ChildHealthStoreTests {
    @Test
    func aChildNeverCheckedReportsUnknown() async {
        let store = ChildHealthStore()
        let health = await store.health(for: "network-gateway")
        #expect(health == .unknown)
    }

    @Test
    func publishRecordsHealthForThatChildOnly() async {
        let store = ChildHealthStore()
        await store.publish(.healthy, for: "network-gateway")

        #expect(await store.health(for: "network-gateway") == .healthy)
        #expect(await store.health(for: "dcal") == .unknown)
        #expect(await store.currentSnapshot() == ["network-gateway": .healthy])
    }

    @Test
    func laterPublishesOverwritePriorHealthForTheSameChild() async {
        let store = ChildHealthStore()
        await store.publish(.unhealthy, for: "network-gateway")
        await store.publish(.healthy, for: "network-gateway")
        #expect(await store.health(for: "network-gateway") == .healthy)
    }

    /// "Health is visible": the store's own publication, independent of any
    /// UI. A subscriber sees the snapshot at subscription time, then again
    /// after each `publish`.
    @Test
    func subscribersObserveEachPublishedChange() async {
        let store = ChildHealthStore()
        var iterator = await store.updates().makeAsyncIterator()

        let initial = await iterator.next()
        #expect(initial == [:])

        await store.publish(.healthy, for: "network-gateway")
        let afterFirstPublish = await iterator.next()
        #expect(afterFirstPublish == ["network-gateway": .healthy])

        await store.publish(.unhealthy, for: "dcal")
        let afterSecondPublish = await iterator.next()
        #expect(afterSecondPublish == ["network-gateway": .healthy, "dcal": .unhealthy])
    }
}
