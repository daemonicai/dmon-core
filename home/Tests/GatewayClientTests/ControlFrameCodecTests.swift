import Testing
@testable import GatewayClient

/// Pins the wire shapes against `core/Dmon.Protocol/Gateway/ControlFrames.cs`
/// and the routing rule in `ControlFrameSerializer.cs`. Decode assertions
/// use literal JSON text, not a Swift value re-encoded and re-decoded by
/// the same code under test, so a serialisation regression (a key
/// strategy, a dropped field, a changed shape) shows up here rather than
/// being masked by round-tripping through the same bug.
@Suite
struct ControlFrameCodecTests {
    // MARK: - Decoding each recognised control frame

    @Test
    func decodesAttach() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"attach","sessionId":"s1","lastSeq":3}"#)
        #expect(frame == .control(.attach(AttachFrame(sessionId: "s1", lastSeq: 3))))
    }

    @Test
    func decodesAttachedWithoutWire() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"attached","generation":1,"headSeq":42}"#)
        #expect(frame == .control(.attached(AttachedFrame(generation: 1, headSeq: 42))))
    }

    @Test
    func decodesAttachedWithWirePresent() throws {
        let frame = try ControlFrameCodec.decode(
            #"{"gw":"attached","generation":1,"headSeq":42,"wire":"0.2"}"#
        )
        #expect(frame == .control(.attached(AttachedFrame(generation: 1, headSeq: 42, wire: "0.2"))))
    }

    @Test
    func decodesAck() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"ack","id":"cmd-1"}"#)
        #expect(frame == .control(.ack(AckFrame(id: "cmd-1"))))
    }

    @Test
    func decodesCreateWithAgent() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"create","agent":"researcher"}"#)
        #expect(frame == .control(.create(CreateFrame(agent: "researcher"))))
    }

    @Test
    func decodesCreateWithoutAgent() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"create"}"#)
        #expect(frame == .control(.create(CreateFrame(agent: nil))))
    }

    @Test
    func decodesCreated() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"created","sessionId":"s1"}"#)
        #expect(frame == .control(.created(CreatedFrame(sessionId: "s1"))))
    }

    @Test
    func decodesCreateRejected() throws {
        let frame = try ControlFrameCodec.decode(
            #"{"gw":"createRejected","code":"unknown_agent","message":"no such agent"}"#
        )
        #expect(frame == .control(.createRejected(
            CreateRejectedFrame(code: "unknown_agent", message: "no such agent")
        )))
    }

    /// `code` is a plain `String`, so a rejection code this client has
    /// never seen still decodes rather than failing.
    @Test
    func decodesCreateRejectedWithAnUnknownCode() throws {
        let frame = try ControlFrameCodec.decode(
            #"{"gw":"createRejected","code":"a_future_code","message":"whatever it means"}"#
        )
        #expect(frame == .control(.createRejected(
            CreateRejectedFrame(code: "a_future_code", message: "whatever it means")
        )))
    }

    @Test
    func decodesPing() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"ping"}"#)
        #expect(frame == .control(.ping))
    }

    @Test
    func decodesPong() throws {
        let frame = try ControlFrameCodec.decode(#"{"gw":"pong"}"#)
        #expect(frame == .control(.pong))
    }

    // MARK: - Routing: three outcomes, not two

    @Test
    func aFrameWithNoGwFieldRoutesAsAnEventCarryingRawTextUnchanged() throws {
        let raw = #"{"type":"turn.start","turnId":"t1"}"#
        let frame = try ControlFrameCodec.decode(raw)
        #expect(frame == .event(raw))
    }

    @Test
    func aNullGwIsTreatedAsNoDiscriminatorNotAnError() throws {
        let raw = #"{"gw":null,"type":"turn.start"}"#
        let frame = try ControlFrameCodec.decode(raw)
        #expect(frame == .event(raw))
    }

    @Test
    func aNonStringGwIsTreatedAsNoDiscriminatorNotAnError() throws {
        let raw = #"{"gw":42,"type":"turn.start"}"#
        let frame = try ControlFrameCodec.decode(raw)
        #expect(frame == .event(raw))
    }

    /// A `gw` this client does not know is its own outcome — it must not
    /// collapse into `.event` (that would desynchronise the client's
    /// ADR-003 sequence counter) and must not be fatal (a gateway adding
    /// a control frame later must not break older clients).
    @Test
    func anUnrecognisedGwValueIsItsOwnOutcomeNotAnEvent() throws {
        let raw = #"{"gw":"somethingNew","foo":"bar"}"#
        let frame = try ControlFrameCodec.decode(raw)
        #expect(frame == .unrecognizedControl(gw: "somethingNew", raw: raw))
    }

    // MARK: - Discriminator position is tolerated

    @Test
    func aDiscriminatorNotFirstInTheObjectStillDecodesCorrectly() throws {
        let frame = try ControlFrameCodec.decode(#"{"sessionId":"s1","gw":"attach","lastSeq":3}"#)
        #expect(frame == .control(.attach(AttachFrame(sessionId: "s1", lastSeq: 3))))
    }

    // MARK: - Malformed input surfaces as a typed failure

    @Test
    func malformedJSONThrowsInvalidJSONRatherThanCrashing() {
        #expect(throws: GatewayFrameDecodingError.invalidJSON) {
            try ControlFrameCodec.decode("{not valid json")
        }
    }

    @Test
    func aRecognisedGwWithAShapeMismatchThrowsInvalidControlFramePayload() {
        #expect(throws: GatewayFrameDecodingError.invalidControlFramePayload(gw: "attach")) {
            try ControlFrameCodec.decode(#"{"gw":"attach"}"#)
        }
    }

    // MARK: - Encoding: camelCase, out-of-position tolerance is moot on encode,
    // nulls omitted

    // Expected literal strings below are in sorted-key order — see
    // `ControlFrameCodec.encodeAsUTF8String`'s doc comment for why encode
    // output is sorted rather than call-order.

    @Test
    func encodesAttachAsCamelCaseLiteralJSON() throws {
        let text = try ControlFrameCodec.encode(.attach(AttachFrame(sessionId: "s1", lastSeq: 3)))
        #expect(text == #"{"gw":"attach","lastSeq":3,"sessionId":"s1"}"#)
    }

    @Test
    func encodesAttachedWithWireOmittedWhenNil() throws {
        let text = try ControlFrameCodec.encode(.attached(AttachedFrame(generation: 1, headSeq: 42)))
        #expect(text == #"{"generation":1,"gw":"attached","headSeq":42}"#)
    }

    @Test
    func encodesAttachedWithWirePresent() throws {
        let text = try ControlFrameCodec.encode(
            .attached(AttachedFrame(generation: 1, headSeq: 42, wire: "0.2"))
        )
        #expect(text == #"{"generation":1,"gw":"attached","headSeq":42,"wire":"0.2"}"#)
    }

    @Test
    func encodesAck() throws {
        let text = try ControlFrameCodec.encode(.ack(AckFrame(id: "cmd-1")))
        #expect(text == #"{"gw":"ack","id":"cmd-1"}"#)
    }

    /// The one assertion this block cannot skip: an absent optional must
    /// be omitted entirely, never emitted as `{"agent":null}`.
    @Test
    func encodingCreateWithNoAgentOmitsTheFieldEntirely() throws {
        let text = try ControlFrameCodec.encode(.create(CreateFrame(agent: nil)))
        #expect(text == #"{"gw":"create"}"#)
        #expect(!text.contains("agent"))
    }

    @Test
    func encodingCreateWithAnAgentIncludesIt() throws {
        let text = try ControlFrameCodec.encode(.create(CreateFrame(agent: "researcher")))
        #expect(text == #"{"agent":"researcher","gw":"create"}"#)
    }

    @Test
    func encodesCreated() throws {
        let text = try ControlFrameCodec.encode(.created(CreatedFrame(sessionId: "s1")))
        #expect(text == #"{"gw":"created","sessionId":"s1"}"#)
    }

    @Test
    func encodesCreateRejected() throws {
        let text = try ControlFrameCodec.encode(
            .createRejected(CreateRejectedFrame(code: "cap_reached", message: "too many sessions"))
        )
        #expect(text == #"{"code":"cap_reached","gw":"createRejected","message":"too many sessions"}"#)
    }

    @Test
    func encodesPing() throws {
        #expect(try ControlFrameCodec.encode(.ping) == #"{"gw":"ping"}"#)
    }

    @Test
    func encodesPong() throws {
        #expect(try ControlFrameCodec.encode(.pong) == #"{"gw":"pong"}"#)
    }
}
