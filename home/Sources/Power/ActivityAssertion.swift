import Foundation

/// Wraps a `ProcessInfo` activity assertion, holding the opaque token
/// returned by `beginActivity(options:reason:)` until it is released.
///
/// Actor isolation serializes `begin()`/`release()`, so beginning while
/// already held is a no-op that keeps the existing token, and releasing
/// while not held is a no-op. Both are safe to call repeatedly.
public actor ActivityAssertion {
    private let options: ProcessInfo.ActivityOptions
    private let reason: String
    private var token: NSObjectProtocol?

    public init(options: ProcessInfo.ActivityOptions, reason: String) {
        self.options = options
        self.reason = reason
    }

    /// Whether the assertion is currently held.
    public var isHeld: Bool {
        token != nil
    }

    /// Begins the activity assertion if it is not already held.
    public func begin() {
        guard token == nil else { return }
        token = ProcessInfo.processInfo.beginActivity(options: options, reason: reason)
    }

    /// Ends the activity assertion if it is held. Safe to call more than once.
    public func release() {
        guard let held = token else { return }
        token = nil
        ProcessInfo.processInfo.endActivity(held)
    }
}
