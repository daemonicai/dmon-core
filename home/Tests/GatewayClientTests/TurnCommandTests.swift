import Foundation
import Testing
@testable import GatewayClient

/// Exercises `TurnCommandCodec.encode(_:)` — the ADR-003 `turn.submit`
/// encoding `GatewaySession.submitTurn(_:)` sends on — in isolation from the
/// session actor and its attach handshake.
@Suite
struct TurnCommandTests {
    @Test
    func encodesTheADR003ShapeWithTheTypeDiscriminatorAndNoGwField() throws {
        let command = TurnSubmitCommand(id: "turn-1", message: "hello")

        let raw = try TurnCommandCodec.encode(command)

        // Sorted keys, matching `ControlFrameCodec.encodeAsUTF8String`'s own
        // precedent — this pins the literal wire bytes, not merely that the
        // right fields exist.
        #expect(raw == #"{"id":"turn-1","message":"hello","type":"turn.submit"}"#)
    }

    /// The message text is arbitrary user input: embedded double quotes, a
    /// backslash, a newline, and a non-BMP emoji (`🐉`, outside the BMP —
    /// encoded as a UTF-16 surrogate pair, unlike a BMP character). A
    /// hand-built JSON string would mishandle at least the quotes and the
    /// backslash; `JSONEncoder` must not.
    ///
    /// Round-trips through `JSONSerialization` — a decoder this type does
    /// not itself use to encode — back to the exact original message,
    /// proving the encoding is correct independent of whatever decoder
    /// happens to read it.
    @Test
    func aMessageWithQuotesBackslashesNewlinesAndANonBMPEmojiRoundTrips() throws {
        let nastyMessage = "she said \"hi\\bye\"\nnew line 🐉 done"

        let raw = try TurnCommandCodec.encode(TurnSubmitCommand(id: "turn-2", message: nastyMessage))

        let data = try #require(raw.data(using: .utf8))
        let decoded = try JSONSerialization.jsonObject(with: data, options: []) as? [String: Any]
        let roundTripped = try #require(decoded?["message"] as? String)

        #expect(roundTripped == nastyMessage)
        #expect(decoded?["type"] as? String == "turn.submit")
        #expect(decoded?["id"] as? String == "turn-2")
    }

    @Test
    func decodingRoundTripsBackToTheOriginalCommand() throws {
        let command = TurnSubmitCommand(id: "turn-3", message: "round trip me")
        let raw = try TurnCommandCodec.encode(command)

        let decoded = try JSONDecoder().decode(TurnSubmitCommand.self, from: #require(raw.data(using: .utf8)))

        #expect(decoded == command)
    }
}
