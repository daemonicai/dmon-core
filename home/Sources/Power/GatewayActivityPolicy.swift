import Foundation

/// Drives an `ActivityAssertion` from a plain "is the gateway enabled" fact,
/// so `Power` never has to know about `Supervisor`'s child descriptors or
/// status types to decide when to hold or release it (design D3: `Power`
/// stays free of a dependency on `Supervisor`).
///
/// Satisfies "The host holds an activity assertion while the gateway is
/// enabled": `apply(gatewayEnabled: true)` begins the assertion,
/// `apply(gatewayEnabled: false)` releases it. Both are idempotent —
/// `ActivityAssertion.begin()`/`release()` already are — so calling this
/// repeatedly, including with the same value twice in a row, is safe.
public struct GatewayActivityPolicy: Sendable {
    private let assertion: ActivityAssertion

    /// `ProcessInfo.h`: "Used for activities that require the computer to
    /// not idle sleep. This is included in `NSActivityUserInitiated`." —
    /// verified against the SDK header, so `.userInitiated` alone already
    /// covers idle-system-sleep prevention. `.idleSystemSleepDisabled` is
    /// named explicitly anyway (bitwise-OR with an already-included flag
    /// is a no-op) so the assertion's coverage of the spec's "user-initiated
    /// work and idle system sleep" is legible at this call site without
    /// requiring the reader to know that fact too.
    public static let defaultOptions: ProcessInfo.ActivityOptions = [.userInitiated, .idleSystemSleepDisabled]

    public static let defaultReason = "dmon-home network gateway is serving"

    public init(
        options: ProcessInfo.ActivityOptions = GatewayActivityPolicy.defaultOptions,
        reason: String = GatewayActivityPolicy.defaultReason
    ) {
        self.assertion = ActivityAssertion(options: options, reason: reason)
    }

    /// Whether the assertion is currently held.
    public var isHolding: Bool {
        get async { await assertion.isHeld }
    }

    /// Begins the assertion when `gatewayEnabled` is `true`, releases it
    /// when `false`.
    public func apply(gatewayEnabled: Bool) async {
        if gatewayEnabled {
            await assertion.begin()
        } else {
            await assertion.release()
        }
    }
}
