import Foundation

/// A raw wire frame could not be turned into a `GatewayFrame`.
///
/// `.invalidJSON` is the only case reachable from malformed input: a frame
/// with no usable `gw` (absent, `null`, or non-string) is not an error —
/// it routes as `GatewayFrame.event`, per the routing rule below. This
/// mirrors `ControlFrameSerializer.GetGwDiscriminator` on the C# side,
/// which sits on the same untrusted network boundary and is deliberately
/// type-tolerant for the same reason: one malformed peer frame must never
/// crash the read loop.
public enum GatewayFrameDecodingError: Error, Hashable, Sendable {
    /// `raw` is not valid JSON.
    case invalidJSON
    /// `raw` carries a recognised `gw` discriminator, but the rest of its
    /// payload does not match that frame's shape.
    case invalidControlFramePayload(gw: String)
}

/// Encodes and decodes gateway connection-control frames, and routes an
/// inbound raw frame to one of `GatewayFrame`'s three outcomes.
///
/// Serialisation matches `WireSerializerOptions.Default` on the C# side:
/// camelCase property names (the Swift property names already are
/// camelCase, so no key-conversion strategy is applied — none is set here,
/// deliberately), an out-of-position discriminator tolerated (`JSONDecoder`
/// is key-order-independent), and null fields omitted (`ControlFrame`'s
/// `Codable` conformances use `encodeIfPresent` for every optional).
public enum ControlFrameCodec {
    /// Routes and decodes one raw wire frame.
    ///
    /// A frame with a top-level `gw` field whose value is a string this
    /// client recognises decodes to `.control`. A frame with no top-level
    /// `gw`, or one that is not usable as a discriminator (absent, JSON
    /// `null`, or a non-string such as `{"gw":42}`), decodes to `.event`
    /// carrying `raw` unchanged — this client models none of an ADR-003
    /// frame's content, so it is never decoded into a Swift type and
    /// re-encoded. A frame with a usable string `gw` this client does not
    /// know decodes to `.unrecognizedControl`, not `.event` — a gateway
    /// that adds a control frame later must not corrupt event routing.
    ///
    /// Throws `GatewayFrameDecodingError.invalidJSON` if `raw` is not
    /// valid JSON, and `.invalidControlFramePayload` if a recognised `gw`
    /// is present but the rest of the object does not match that frame's
    /// shape.
    public static func decode(_ raw: String) throws -> GatewayFrame {
        guard let data = raw.data(using: .utf8) else {
            throw GatewayFrameDecodingError.invalidJSON
        }

        let jsonObject: Any
        do {
            jsonObject = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        } catch {
            throw GatewayFrameDecodingError.invalidJSON
        }

        guard let gw = gwDiscriminator(fromParsedJSON: jsonObject) else {
            return .event(raw)
        }

        do {
            let decoder = JSONDecoder()
            switch gw {
            case "attach":
                return .control(.attach(try decoder.decode(AttachFrame.self, from: data)))
            case "attached":
                return .control(.attached(try decoder.decode(AttachedFrame.self, from: data)))
            case "ack":
                return .control(.ack(try decoder.decode(AckFrame.self, from: data)))
            case "create":
                return .control(.create(try decoder.decode(CreateFrame.self, from: data)))
            case "created":
                return .control(.created(try decoder.decode(CreatedFrame.self, from: data)))
            case "createRejected":
                return .control(.createRejected(try decoder.decode(CreateRejectedFrame.self, from: data)))
            case "ping":
                return .control(.ping)
            case "pong":
                return .control(.pong)
            default:
                return .unrecognizedControl(gw: gw, raw: raw)
            }
        } catch {
            throw GatewayFrameDecodingError.invalidControlFramePayload(gw: gw)
        }
    }

    /// Encodes a control frame for the wire.
    public static func encode(_ frame: GatewayControlFrame) throws -> String {
        switch frame {
        case .attach(let payload):
            return try encodeAsUTF8String(payload)
        case .attached(let payload):
            return try encodeAsUTF8String(payload)
        case .ack(let payload):
            return try encodeAsUTF8String(payload)
        case .create(let payload):
            return try encodeAsUTF8String(payload)
        case .created(let payload):
            return try encodeAsUTF8String(payload)
        case .createRejected(let payload):
            return try encodeAsUTF8String(payload)
        case .ping:
            return #"{"gw":"ping"}"#
        case .pong:
            return #"{"gw":"pong"}"#
        }
    }

    /// JSON object member order carries no meaning on this wire — the
    /// "discriminator position tolerated" requirement exists precisely
    /// because nothing may depend on it — and this platform's
    /// `JSONEncoder` does not preserve `encode(to:)` call order (its
    /// intermediate representation is an unordered dictionary). Sorting
    /// keys makes the emitted bytes deterministic without claiming an
    /// ordering guarantee the wire does not have.
    ///
    /// Note what this makes the client depend on, since it is not visible
    /// from here: sorting puts `gw` after `agent` in an outbound `create`,
    /// so the network host must tolerate an out-of-position discriminator
    /// on the frames this client *sends*, not merely on the ones it
    /// receives. It does — verified against the two parse paths that see
    /// them, `ControlFrameSerializer.GetGwDiscriminator` (`JsonNode.Parse`,
    /// a DOM read) and `ParseAttach`/`ParseCreate`
    /// (`JsonSerializer.Deserialize`, order-independent by contract).
    /// Neither reads a first property. A host that ever parsed these
    /// positionally would break this client with no Swift test able to see
    /// it.
    private static func encodeAsUTF8String(_ payload: some Encodable) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(payload)
        return String(decoding: data, as: UTF8.self)
    }

    /// Returns the top-level `"gw"` value if `jsonObject` is a JSON object
    /// whose `"gw"` member is a JSON string, or `nil` otherwise (not an
    /// object, no `"gw"` member, `"gw"` is `null`, or `"gw"` is a
    /// non-string). `NSNull` and every other `JSONSerialization` leaf type
    /// fail the `as? String` cast, so this is naturally type-tolerant
    /// without needing to special-case `null`.
    private static func gwDiscriminator(fromParsedJSON jsonObject: Any) -> String? {
        (jsonObject as? [String: Any])?["gw"] as? String
    }
}
