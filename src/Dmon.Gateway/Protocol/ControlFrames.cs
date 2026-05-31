using System.Text.Json.Serialization;

namespace Dmon.Gateway.Protocol;

// ---------------------------------------------------------------------------
// Connection-control frame DTOs (ADR-012 / Group 3)
//
// Discriminator field: "gw"
//
// ADR-003 frames use a top-level "type" field as their discriminator
// (commands: {"id":..., "type":"commandName", ...}, events: {"type":"eventName", ...}).
// Control frames use "gw" instead — a field that ADR-003 never emits — so routing
// is unambiguous: a frame with "gw" is a control frame; a frame without "gw" is an
// ADR-003 command or event forwarded byte-unchanged.
//
// Wire shapes:
//   attach  (client → gateway): {"gw":"attach","sessionId":"...","lastSeq":N}
//   attached (gateway → client): {"gw":"attached","generation":G,"headSeq":H}
//   ack     (gateway → client): {"gw":"ack","id":"..."}
//   ping    (either direction):  {"gw":"ping"}
//   pong    (either direction):  {"gw":"pong"}
// ---------------------------------------------------------------------------

/// <summary>
/// Client → gateway: open or resume a session.
/// </summary>
public sealed record AttachFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "attach";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Last sequence number seen by the client. Group 4 uses this for replay;
    /// accepted here but not acted upon until Group 4.
    /// </summary>
    [JsonPropertyName("lastSeq")]
    public long LastSeq { get; init; }
}

/// <summary>
/// Gateway → client: attach accepted.
/// </summary>
public sealed record AttachedFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "attached";

    /// <summary>
    /// Monotonically increasing counter incremented on each Attach. Group 6 uses this
    /// to fence stale connections; issued here but not enforced until Group 6.
    /// </summary>
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    /// <summary>
    /// Highest sequence number persisted for this session. Placeholder 0 until Group 4
    /// wires up messages.jsonl-backed sequencing.
    /// </summary>
    [JsonPropertyName("headSeq")]
    public required long HeadSeq { get; init; }
}

/// <summary>
/// Gateway → client: command acknowledged. Defined here; generation/dedup logic is Group 5.
/// </summary>
public sealed record AckFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "ack";

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>
/// Either direction: liveness probe.
/// </summary>
public sealed record PingFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "ping";
}

/// <summary>
/// Either direction: liveness reply.
/// </summary>
public sealed record PongFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "pong";
}
