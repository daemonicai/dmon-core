using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dmon.Gateway.Protocol;

/// <summary>
/// Serializes outbound control frames and routes inbound frames.
/// Routing rule: a frame with a top-level "gw" field is a control frame;
/// a frame without one is an ADR-003 command forwarded byte-unchanged.
/// </summary>
public static class ControlFrameSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize<T>(T frame) =>
        JsonSerializer.Serialize(frame, Options);

    /// <summary>
    /// Returns the "gw" discriminator value if the raw frame text is a control frame,
    /// or <c>null</c> if it is an ADR-003 frame (no "gw" field), the field is null, or the
    /// field is present but not a JSON string. The read is type-tolerant: a structurally-valid
    /// frame with a non-string "gw" (e.g. <c>{"gw":42}</c>) is treated as having no usable
    /// discriminator rather than throwing — this parser sits on the untrusted network boundary,
    /// so one malformed client frame must never crash the forwarding loop.
    /// </summary>
    public static string? GetGwDiscriminator(string rawFrame) =>
        TryReadTopLevelString(rawFrame, "gw");

    private static string? TryReadTopLevelString(string rawFrame, string propertyName)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(rawFrame);
            if (node?[propertyName] is not JsonValue value)
                return null;
            // TryGetValue<string> returns false for a structurally-valid-but-non-string value
            // (number, bool, object), so a non-string id/gw yields null instead of throwing.
            return value.TryGetValue(out string? text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the top-level "id" field of an ADR-003 command frame, or <c>null</c> if absent,
    /// null, not a JSON string, or the frame is not valid JSON. The read is type-tolerant: a
    /// frame with a non-string id (e.g. <c>{"id":42}</c>) is treated as having no usable id
    /// rather than throwing, so a malformed client frame on the untrusted boundary cannot crash
    /// the forwarding loop. The raw frame is parsed into a throwaway <see cref="JsonNode"/> and
    /// discarded; the original bytes are never re-serialized (ADR-003: forward unchanged).
    /// </summary>
    public static string? GetCommandId(string rawFrame) =>
        TryReadTopLevelString(rawFrame, "id");

    /// <summary>
    /// Returns the top-level "type" field of an ADR-003 command frame, or <c>null</c> if absent,
    /// null, not a JSON string, or the frame is not valid JSON. Used by the forwarding loop to
    /// identify permission-response commands (<c>tool.confirmResponse</c>,
    /// <c>ui.inputResponse</c>) so outstanding-request tracking can be cleared on a successful
    /// core write.
    /// </summary>
    public static string? GetCommandType(string rawFrame) =>
        TryReadTopLevelString(rawFrame, "type");

    /// <summary>
    /// Parses a raw frame as an <see cref="AttachFrame"/>. Returns <c>null</c> on parse failure.
    /// </summary>
    public static AttachFrame? ParseAttach(string rawFrame)
    {
        try
        {
            return JsonSerializer.Deserialize<AttachFrame>(rawFrame, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
