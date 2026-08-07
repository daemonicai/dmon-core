import Foundation

/// Client → core: submit a turn. Mirrors `TurnSubmitCommand` in
/// `core/Dmon.Protocol/Commands/TurnCommands.cs`, discriminated by the
/// `Command` base type's `"type"` property (ADR-003), not by `ControlFrame`'s
/// `"gw"` — this is deliberately a *different* shape from every type in
/// `ControlFrame.swift`, and `GatewayConnection.sendCommand(_:)` is what
/// keeps the two from ever being sent through the same path.
///
/// `images` is never modelled: this block's scope is text only (see the
/// task brief), and `TurnSubmitCommand.Images` is optional on the C# side,
/// so omitting it entirely is a valid `null`-free encoding, not a partial
/// one.
///
/// Hand-writes `Codable` for the same reason every type in
/// `ControlFrame.swift` does: `"type"` is a literal baked into
/// `encode(to:)`, never a settable stored property.
struct TurnSubmitCommand: Hashable, Sendable {
    var id: String
    var message: String

    init(id: String, message: String) {
        self.id = id
        self.message = message
    }
}

extension TurnSubmitCommand: Codable {
    private enum CodingKeys: String, CodingKey {
        case type, id, message
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        message = try container.decode(String.self, forKey: .message)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode("turn.submit", forKey: .type)
        try container.encode(id, forKey: .id)
        try container.encode(message, forKey: .message)
    }
}

/// Encodes an ADR-003 command for the wire. A sibling to
/// `ControlFrameCodec`, not a method folded into it — that type's own doc
/// comment states it models no ADR-003 frame content, and this encoder is
/// exactly that content, for the one command this block implements.
enum TurnCommandCodec {
    /// Encodes `command` with `JSONEncoder`, never string interpolation —
    /// `message` is arbitrary user text (quotes, backslashes, newlines,
    /// emoji, control characters), and a hand-built JSON string cannot
    /// represent it safely.
    ///
    /// Keys are sorted for the same reason `ControlFrameCodec
    /// .encodeAsUTF8String(_:)` sorts them — `JSONEncoder` does not
    /// otherwise guarantee `encode(to:)` call order, so sorting is what
    /// makes the emitted bytes deterministic — but *why sorting is safe to
    /// do* is not the same reason in both cases, and the difference is a
    /// real cross-language coupling, not a stylistic echo.
    ///
    /// A control frame is read by the gateway's own hand-rolled
    /// `ControlFrameSerializer.GetGwDiscriminator`, which scans for `"gw"`
    /// regardless of position — order never mattered there in the first
    /// place. **This command is read by something else entirely**: the
    /// gateway forwards it unparsed to the core, which deserializes it
    /// through `System.Text.Json` polymorphism keyed on `"type"`
    /// (`core/Dmon.Core/Rpc/CommandDispatcher.cs`, `doc.RootElement
    /// .Deserialize<Command>(WireSerializerOptions.Default)`). Sorting puts
    /// `"id"` before `"type"` on the wire, and that only parses because
    /// `core/Dmon.Protocol/WireSerializerOptions.cs` sets
    /// `AllowOutOfOrderMetadataProperties = true` — whose own doc comment
    /// states exactly this: commands carry `"id"` before `"type"`, so the
    /// deserializer must tolerate an out-of-position discriminator.
    ///
    /// **This module depends on that flag staying `true`, and no test in
    /// this Swift package can see it flip.** If `WireSerializerOptions`
    /// ever drops `AllowOutOfOrderMetadataProperties`, every `turn.submit`
    /// this client sends starts failing to deserialize on the core side —
    /// silently, from this side of the boundary, since `TurnCommandCodec`
    /// itself has nothing to validate against. Recorded here so the
    /// dependency is discoverable at the point it was created, not only
    /// from a production failure.
    static func encode(_ command: TurnSubmitCommand) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(command)
        return String(decoding: data, as: UTF8.self)
    }
}
