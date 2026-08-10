import Foundation
import os
@testable import GatewayClient

/// Hands out a fresh `InMemoryGatewayTransport` per call, in creation
/// order, and lets a test inspect each one afterwards by index. This is
/// what turns a claim like "no second connection is ever created after a
/// rejection" into something a test can actually falsify — without
/// recording every transport `GatewaySession` asks for, a test could only
/// inspect whichever single transport it happened to hold a reference to.
///
/// `GatewaySession.init(makeTransport:)` accepts `@Sendable () async throws
/// -> any GatewayTransport` (widened from a plain, non-`async`, non-`throws`
/// closure by block B5, to let a caller resolve a device-key credential
/// before connecting) — but a synchronous, non-throwing closure like this
/// one still converts to that type implicitly, so this factory has no need
/// to go through an actor call itself: `makeTransport` here returns
/// synchronously by this type's own choice, not because `GatewaySession`
/// requires it. `OSAllocatedUnfairLock` guards the recorded list instead: the same
/// real-synchronisation-primitive choice `Supervisor/ProcessExit.swift`'s
/// `ExitWaitBox` documents, over `@unchecked Sendable` /
/// `nonisolated(unsafe)` (design D14 reserves those for the audio ring
/// buffer only).
final class RecordingTransportFactory: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: [InMemoryGatewayTransport]())

    /// A closure suitable for `GatewaySession.init(makeTransport:)` that
    /// records every transport it creates before returning it.
    var makeTransport: @Sendable () -> any GatewayTransport {
        { [self] in
            let transport = InMemoryGatewayTransport()
            state.withLock { $0.append(transport) }
            return transport
        }
    }

    /// Every transport created so far, in creation order.
    func transports() -> [InMemoryGatewayTransport] {
        state.withLock { $0 }
    }

    /// The transport created at `index`, or `nil` if none has been created
    /// there yet.
    func transport(at index: Int) -> InMemoryGatewayTransport? {
        state.withLock { transports in
            transports.indices.contains(index) ? transports[index] : nil
        }
    }

    /// How many transports have been created so far.
    func count() -> Int {
        state.withLock { $0.count }
    }
}
