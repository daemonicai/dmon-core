import Foundation

/// The production `GatewayTransport` conformer, backed by
/// `URLSessionWebSocketTask`.
///
/// An `actor`, not a `class` with a lock: `URLSessionWebSocketTask` is not
/// `Sendable`, so anything that holds one across concurrent access needs
/// either actor isolation or `@unchecked Sendable` plus a manually
/// enforced lock. `@unchecked Sendable` is reserved for the audio
/// ring-buffer boundary (design D14) and appears nowhere else in this
/// tree; an actor gives the same safety for free and without a suppressed
/// diagnostic.
///
/// Deliberately thin: everything that is not a direct `URLSession` call is
/// factored into `closedError(closeCode:reasonData:)` and `request(for:)`,
/// pure static functions, so the mapping from a raw close code to the
/// typed error — and header assembly — are testable without a live
/// socket. What remains untested by `GatewayClientTests` is the
/// `URLSession` calls themselves.
public actor WebSocketGatewayTransport: GatewayTransport {
    /// Tracks which of `GatewayTransportError`'s three lifecycle facts
    /// this transport is in, so `send`/`receive` never has to infer it
    /// from whether a task reference happens to be `nil` — the previous
    /// shape, where `close()` set `task = nil`, made "never connected"
    /// and "connected then closed" indistinguishable from the stored
    /// state alone.
    private enum ConnectionState {
        case notConnected
        case connected(URLSessionWebSocketTask)
        case closedLocally
    }

    private let endpoint: GatewayEndpoint
    private let session: URLSession
    private var state: ConnectionState = .notConnected

    public init(endpoint: GatewayEndpoint = GatewayEndpoint(), session: URLSession = .shared) {
        self.endpoint = endpoint
        self.session = session
    }

    public func connect() async throws {
        let task = session.webSocketTask(with: Self.request(for: endpoint))
        task.resume()
        state = .connected(task)
    }

    public func send(_ frame: String) async throws {
        guard case .connected(let task) = state else {
            throw stateError()
        }
        do {
            try await task.send(.string(frame))
        } catch {
            throw closedError(from: task) ?? error
        }
    }

    public func receive() async throws -> String {
        guard case .connected(let task) = state else {
            throw stateError()
        }
        let message: URLSessionWebSocketTask.Message
        do {
            message = try await task.receive()
        } catch {
            throw closedError(from: task) ?? error
        }

        switch message {
        case .string(let text):
            return text
        case .data:
            throw GatewayTransportError.binaryFrameReceived
        @unknown default:
            throw GatewayTransportError.binaryFrameReceived
        }
    }

    /// Closing a transport that was never connected leaves it
    /// `.notConnected` rather than moving it to `.closedLocally` — there
    /// was never a connection to close. Closing an already-closed
    /// transport is a no-op; `close()` is safe to call more than once.
    public func close() async {
        guard case .connected(let task) = state else { return }
        task.cancel(with: .normalClosure, reason: nil)
        state = .closedLocally
    }

    private func stateError() -> GatewayTransportError {
        switch state {
        case .notConnected:
            return .notConnected
        case .closedLocally:
            return .closedLocally
        case .connected:
            preconditionFailure("stateError() must only be called when state is not .connected")
        }
    }

    private func closedError(from task: URLSessionWebSocketTask) -> GatewayTransportError? {
        Self.closedError(closeCode: task.closeCode.rawValue, reasonData: task.closeReason)
    }

    /// `closeCode == 0` (`URLSessionWebSocketTask.CloseCode.invalid`)
    /// means the task was never closed with a code — a plain network
    /// failure rather than a peer-initiated close — so this returns `nil`
    /// and the caller surfaces the underlying `URLSession` error instead.
    static func closedError(closeCode: Int, reasonData: Data?) -> GatewayTransportError? {
        guard closeCode != 0 else { return nil }
        let reason = reasonData.flatMap { String(data: $0, encoding: .utf8) } ?? ""
        return .closed(code: GatewayCloseCode(rawValue: closeCode), reason: reason)
    }

    /// Assembles the connect request from `endpoint`'s URL and headers.
    /// Factored out (rather than inlined in `connect()`) so header
    /// assembly is testable without a live socket.
    static func request(for endpoint: GatewayEndpoint) -> URLRequest {
        var request = URLRequest(url: endpoint.url)
        for (name, value) in endpoint.headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        return request
    }
}
