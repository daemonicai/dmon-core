import Foundation

/// The wire-level close codes the dmon network gateway (`Dmon.Network`)
/// closes a connection with, plus the ordinary WebSocket normal closure.
///
/// Mirrors `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` exactly —
/// collapsing these into "connection closed" would throw away the only
/// material a caller has for an actionable message (a session that no
/// longer exists reads very differently to a connection fenced out by a
/// newer attach). `.other` preserves any code this client does not yet
/// have a name for, so an unrecognised close is still observable rather
/// than silently discarded.
public enum GatewayCloseCode: Hashable, Sendable {
    /// RFC 6455 1000: an ordinary, expected close.
    case normal
    /// RFC 6455 1009: the message exceeded the gateway's size limit.
    case messageTooBig
    /// 4400: the client violated the wire protocol (malformed frame,
    /// wrong first frame, or a binary frame — this client never sends
    /// binary, but the gateway also uses 4400 for that case).
    case protocolViolation
    /// 4404: `attach` named a session the gateway does not have.
    case unknownSession
    /// 4409: this connection was fenced out by a newer `attach` to the
    /// same session.
    case supersededByNewerAttach
    /// 4500: the gateway failed to spawn or hand shake with the core.
    case coreFailure
    /// Any other close code, preserved verbatim.
    case other(Int)

    public init(rawValue: Int) {
        switch rawValue {
        case 1000: self = .normal
        case 1009: self = .messageTooBig
        case 4400: self = .protocolViolation
        case 4404: self = .unknownSession
        case 4409: self = .supersededByNewerAttach
        case 4500: self = .coreFailure
        default: self = .other(rawValue)
        }
    }

    public var rawValue: Int {
        switch self {
        case .normal: 1000
        case .messageTooBig: 1009
        case .protocolViolation: 4400
        case .unknownSession: 4404
        case .supersededByNewerAttach: 4409
        case .coreFailure: 4500
        case .other(let code): code
        }
    }
}

/// Errors a `GatewayTransport` conformer can raise. Distinct from any
/// error type a concrete transport (`URLSession`, `Network.framework`, an
/// in-memory test double) would raise on its own, so nothing above the
/// abstraction ever needs to know which transport is underneath.
///
/// A connection's lifecycle has exactly three distinguishable end states,
/// and every conformer (`WebSocketGatewayTransport`,
/// `InMemoryGatewayTransport`) must report all three the same way:
///
/// 1. It was never connected — `.notConnected`, and *only* this.
/// 2. It was closed locally, by calling `close()` — `.closedLocally`, and
///    *only* this.
/// 3. It was closed from the peer's side, carrying a code and reason —
///    `.closed(code:reason:)`, and *only* this.
///
/// These must not collapse into each other. A self-initiated close (the
/// app quitting, or switching sessions) is a normal exit and must not be
/// mistaken for a gateway failure or trigger a reconnect; a peer close
/// carrying `4409` (superseded by a newer attach) or `4500` (core
/// failure) is the opposite, and the connection actor above this
/// abstraction (and, later, its reconnect policy) branches on the
/// distinction. Calling `close()` on a transport that was never
/// connected leaves it `.notConnected` — it does not become
/// `.closedLocally`, since there was never a connection to close.
public enum GatewayTransportError: Error, Hashable, Sendable {
    /// `send`/`receive` was called before `connect()` ever succeeded.
    /// This is the *only* case this covers: once a connection has been
    /// established, its later close — local or peer-initiated — is
    /// reported as `.closedLocally` or `.closed(code:reason:)`, never
    /// regressed back to `.notConnected`.
    case notConnected
    /// The peer sent a binary frame. This client is text-only (ADR-003:
    /// the wire protocol is text JSONL), so a binary frame is never a
    /// value to hand upward — it is always a protocol error.
    case binaryFrameReceived
    /// `close()` was called locally, after a successful `connect()`.
    case closedLocally
    /// The connection closed from the peer's side, carrying the close
    /// code and reason the peer sent (or that the transport observed)
    /// so a caller can render an actionable message instead of a
    /// generic "disconnected".
    case closed(code: GatewayCloseCode, reason: String)
}

/// Abstracts the transport beneath the gateway client so that no type
/// above this protocol ever names a concrete transport (`URLSession`,
/// `URLSessionWebSocketTask`, `Network.framework`, …).
///
/// `receive()` is pull-based, one frame per call — mirroring
/// `URLSessionWebSocketTask.receive()` — rather than an `AsyncStream`
/// property. That keeps frame buffering out of the transport: ownership
/// of the read loop, and of the sequence counter that must observe every
/// frame in order, belongs to the connection actor above this
/// abstraction, not to the transport underneath it.
public protocol GatewayTransport: Sendable {
    /// Establishes the connection. Idempotent conformers are not
    /// required; callers connect once per transport instance.
    func connect() async throws

    /// Sends one text frame.
    func send(_ frame: String) async throws

    /// Awaits and returns the next text frame. Throws
    /// `GatewayTransportError.binaryFrameReceived` if the peer sends a
    /// binary frame, `.closedLocally` if `close()` was called locally,
    /// or `.closed(code:reason:)` once the peer has closed the
    /// connection.
    func receive() async throws -> String

    /// Closes the connection. Safe to call more than once.
    func close() async
}

/// Where to reach the gateway, and what headers to present when
/// connecting.
///
/// The endpoint is configuration, not an assumption (design D2, PRD
/// §7.4): a caller must always name a URL explicitly — `defaultURL`
/// is an *opt-in* constant for a caller that wants `Dmon.Network`'s own
/// default bind address, never an implicit fallback this initialiser
/// supplies on its own. `headers` carries whatever the caller assembles
/// — including, via `GatewayEndpoint.headers(for:additionalHeaders:)`
/// below, an `Authorization: Bearer …` entry for a `DeviceKeySecret`.
public struct GatewayEndpoint: Hashable, Sendable {
    public var url: URL
    public var headers: [String: String]

    /// `Dmon.Network`'s own default bind address and WebSocket path
    /// (`frontends/Dmon.Network/appsettings.json`'s `Network:BindAddress`,
    /// `http://127.0.0.1:5500`, mapped to `/ws` in `Program.cs`) — a
    /// constant a caller opts into by passing it explicitly, not a
    /// fallback this module assumes on a caller's behalf. Co-location
    /// with `Dmon.Network` is a call-site choice, not a default baked
    /// into a module written to extract onto iOS unmodified (design D16).
    public static let defaultURL = URL(string: "ws://127.0.0.1:5500/ws")!

    public init(url: URL, headers: [String: String] = [:]) {
        self.url = url
        self.headers = headers
    }

    /// The one place that decides whether an `Authorization` header is
    /// present at all: a `credential` contributes exactly one
    /// `Authorization: Bearer <secret>` entry; `nil` contributes none —
    /// not an empty or placeholder value, no key at all.
    ///
    /// The credential's entry always wins on a key collision. If
    /// `additionalHeaders` already contains `Authorization` and a
    /// `credential` is also supplied, the credential's `Bearer <secret>`
    /// value overwrites it — the caller-supplied value is discarded, not
    /// merged or preserved. Every other key in `additionalHeaders` is
    /// carried through unchanged. A caller-supplied `Authorization` is
    /// only meaningful when there is no credential, in which case it
    /// passes through untouched like any other header.
    ///
    /// This client cannot see whether the gateway's device-key store is
    /// populated or its bind address is loopback — it presents whatever
    /// credential it holds, or none, and the host decides whether that is
    /// acceptable (ADR-036). Under D13's self-provisioning the two
    /// coincide by construction — the host holds its own store entry
    /// exactly when it also holds a credential to present here — but that
    /// is an invariant of how the credential came to exist, not a check
    /// this function performs.
    public static func headers(
        for credential: DeviceKeySecret?,
        additionalHeaders: [String: String] = [:]
    ) -> [String: String] {
        var headers = additionalHeaders
        if let credential {
            headers["Authorization"] = "Bearer \(credential.secret)"
        }
        return headers
    }
}
