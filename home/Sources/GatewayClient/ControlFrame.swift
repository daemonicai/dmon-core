import Foundation

// ---------------------------------------------------------------------------
// Connection-control frame value types, mirroring
// `core/Dmon.Protocol/Gateway/ControlFrames.cs` exactly.
//
// Discriminator field: "gw". ADR-003 frames use a top-level "type" field
// instead — a field control frames never emit — so routing is unambiguous:
// a frame with "gw" is a control frame, a frame without one is an ADR-003
// command or event forwarded byte-unchanged (`ControlFrameCodec` implements
// the routing).
//
// Each type hand-writes `Codable` rather than relying on synthesis so that
// "gw" is a literal baked into `encode(to:)` — never a settable stored
// property — matching the C# side's get-only `Gw => "literal"` computed
// property. Optional fields use `encodeIfPresent`/`decodeIfPresent` so an
// absent value is omitted on the wire, never emitted as `null`.
// ---------------------------------------------------------------------------

/// Client → gateway: open or resume a session.
public struct AttachFrame: Hashable, Sendable {
    public var sessionId: String

    /// Last sequence number seen by the client. The gateway replays events
    /// with seq greater than this value up to `headSeq` before resuming
    /// live delivery.
    public var lastSeq: Int64

    public init(sessionId: String, lastSeq: Int64) {
        self.sessionId = sessionId
        self.lastSeq = lastSeq
    }
}

extension AttachFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, sessionId, lastSeq
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try container.decode(String.self, forKey: .sessionId)
        lastSeq = try container.decode(Int64.self, forKey: .lastSeq)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("attach", forKey: .gw)
        try container.encode(sessionId, forKey: .sessionId)
        try container.encode(lastSeq, forKey: .lastSeq)
    }
}

/// Gateway → client: attach accepted.
public struct AttachedFrame: Hashable, Sendable {
    /// Monotonically increasing counter incremented on each attach. Used
    /// to fence stale connections.
    public var generation: Int64

    /// Highest sequence number assigned to a server → client event for
    /// this session at the moment of attach. Zero if no events have been
    /// emitted yet.
    public var headSeq: Int64

    /// The host's `Major.Minor` wire protocol version (task 6.6, landing
    /// in a later block on the C# side). Optional because a gateway that
    /// predates that field never sends it. This type only carries the
    /// value decoded from the wire; what an absent value means is a later
    /// block's policy decision, not this one's.
    public var wire: String?

    public init(generation: Int64, headSeq: Int64, wire: String? = nil) {
        self.generation = generation
        self.headSeq = headSeq
        self.wire = wire
    }
}

extension AttachedFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, generation, headSeq, wire
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        generation = try container.decode(Int64.self, forKey: .generation)
        headSeq = try container.decode(Int64.self, forKey: .headSeq)
        wire = try container.decodeIfPresent(String.self, forKey: .wire)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("attached", forKey: .gw)
        try container.encode(generation, forKey: .generation)
        try container.encode(headSeq, forKey: .headSeq)
        try container.encodeIfPresent(wire, forKey: .wire)
    }
}

/// Gateway → client: command acknowledged.
public struct AckFrame: Hashable, Sendable {
    public var id: String

    public init(id: String) {
        self.id = id
    }
}

extension AckFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, id
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("ack", forKey: .gw)
        try container.encode(id, forKey: .id)
    }
}

/// Client → gateway: create a new session, optionally selecting a named
/// agent. On success the gateway replies `CreatedFrame`; the client then
/// sends `AttachFrame` with the returned session id.
public struct CreateFrame: Hashable, Sendable {
    /// Agent name to activate for the new session. `nil` selects the
    /// default agent and is omitted from the wire rather than sent as
    /// `null`.
    public var agent: String?

    public init(agent: String? = nil) {
        self.agent = agent
    }
}

extension CreateFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, agent
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        agent = try container.decodeIfPresent(String.self, forKey: .agent)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("create", forKey: .gw)
        try container.encodeIfPresent(agent, forKey: .agent)
    }
}

/// Gateway → client: session created successfully. The client must follow
/// with `AttachFrame` using the returned `sessionId` and `lastSeq: 0`.
public struct CreatedFrame: Hashable, Sendable {
    public var sessionId: String

    public init(sessionId: String) {
        self.sessionId = sessionId
    }
}

extension CreatedFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, sessionId
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        sessionId = try container.decode(String.self, forKey: .sessionId)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("created", forKey: .gw)
        try container.encode(sessionId, forKey: .sessionId)
    }
}

/// Gateway → client: session creation rejected.
public struct CreateRejectedFrame: Hashable, Sendable {
    /// Machine-readable error identifier. Known values today are
    /// `unknown_agent`, `cap_reached` and `core_timeout`, but this is
    /// modelled as a plain `String`, not a closed enum: a client that
    /// cannot represent a rejection code the gateway adds later is worse
    /// than one that merely displays it verbatim.
    public var code: String

    /// Human-readable, actionable message suitable for direct display to
    /// the user.
    public var message: String

    public init(code: String, message: String) {
        self.code = code
        self.message = message
    }
}

extension CreateRejectedFrame: Codable {
    private enum CodingKeys: String, CodingKey {
        case gw, code, message
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        code = try container.decode(String.self, forKey: .code)
        message = try container.decode(String.self, forKey: .message)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("createRejected", forKey: .gw)
        try container.encode(code, forKey: .code)
        try container.encode(message, forKey: .message)
    }
}

/// A decoded, recognised connection-control frame. `ping` and `pong` carry
/// no payload beyond the discriminator, so they have no associated value.
public enum GatewayControlFrame: Hashable, Sendable {
    case attach(AttachFrame)
    case attached(AttachedFrame)
    case ack(AckFrame)
    case create(CreateFrame)
    case created(CreatedFrame)
    case createRejected(CreateRejectedFrame)
    case ping
    case pong
}

/// The outcome of routing one raw wire frame. There are three, not two:
/// a frame this client recognises as a control frame decodes to
/// `.control`; a frame with no usable `gw` discriminator is an ADR-003
/// command or event and is never modelled, only carried as the raw text
/// the gateway forwarded byte-unchanged; and a frame carrying a `gw` value
/// this client does not (yet) know is its own outcome, `.unrecognizedControl`
/// — forward compatibility with a gateway that adds a control frame later.
/// It must never collapse into `.event`: only an `.event` outcome may ever
/// advance the client's ADR-003 sequence counter (`seq` is gateway-local
/// and never appears on the wire the client sees — the only sequence
/// number the client is ever told is `headSeq` on `attached`), so mis-
/// routing a control frame as an event would desynchronise that count.
public enum GatewayFrame: Hashable, Sendable {
    case control(GatewayControlFrame)
    case event(String)
    case unrecognizedControl(gw: String, raw: String)
}
