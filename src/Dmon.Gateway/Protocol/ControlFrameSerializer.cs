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
    /// or <c>null</c> if it is an ADR-003 frame (no "gw" field).
    /// </summary>
    public static string? GetGwDiscriminator(string rawFrame)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(rawFrame);
            return node?["gw"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
