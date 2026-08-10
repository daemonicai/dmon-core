using System.Text.Json.Serialization;
using Dmon.Protocol;

namespace Dmon.Protocol.Gateway;

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
//   attached (gateway → client): {"gw":"attached","generation":G,"headSeq":H,"wire":"Major.Minor"}
//   ack     (gateway → client): {"gw":"ack","id":"..."}
//   ping    (either direction):  {"gw":"ping"}
//   pong    (either direction):  {"gw":"pong"}
//   create  (client → gateway): {"gw":"create","agent":"..."}   (agent optional)
//   created (gateway → client): {"gw":"created","sessionId":"..."}
//   createRejected (gateway → client): {"gw":"createRejected","code":"...","message":"..."}
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
    /// Last sequence number seen by the client. The gateway replays events
    /// with seq greater than this value up to <c>headSeq</c> before resuming live delivery.
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
    /// Monotonically increasing counter incremented on each Attach. The gateway fences stale
    /// connections by this value: attaching a new connection for the session evicts and aborts
    /// the prior one, so only the connection holding the current generation may keep sending.
    /// </summary>
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    /// <summary>
    /// Highest sequence number assigned to a server→client event for this session at the
    /// moment of attach. Zero if no events have been emitted yet.
    /// </summary>
    [JsonPropertyName("headSeq")]
    public required long HeadSeq { get; init; }

    /// <summary>
    /// The <c>Major.Minor</c> wire protocol version this host implements, sourced from
    /// <see cref="ProtocolVersion.Current"/>. A client compares this against its own
    /// supported version on attach, so a mismatch surfaces here rather than later as a
    /// malformed frame or an unrecognised event.
    /// </summary>
    [JsonPropertyName("wire")]
    public string Wire => ProtocolVersion.Current;
}

/// <summary>
/// Gateway → client: command acknowledged. A client may safely resend a command whose ack
/// it never saw: if the original reached core, the gateway recognises the resend by
/// <c>id</c> and does not forward it again; if it did not, the resend is admitted and
/// forwarded as normal. Either way the command is delivered to core at most once and never
/// silently dropped.
/// </summary>
public sealed record AckFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "ack";

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>
/// Client → gateway: create a new session, optionally selecting a named agent.
/// An agent is a <c>.cs</c> composition root under the gateway workspace root.
/// On success the gateway replies <see cref="CreatedFrame"/>; the client then sends
/// <see cref="AttachFrame"/> with the returned session id.
/// </summary>
public sealed record CreateFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "create";

    /// <summary>
    /// Agent name to activate for the new session.
    /// Null selects the default agent (root <c>Dmon.cs</c>).
    /// An unknown agent name causes the gateway to reply <c>createRejected {code="unknown_agent"}</c>.
    /// </summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }
}

/// <summary>
/// Gateway → client: session created successfully. The client must follow with
/// <see cref="AttachFrame"/> using the returned <paramref name="SessionId"/> and
/// <c>lastSeq: 0</c>.
/// </summary>
public sealed record CreatedFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "created";

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}

/// <summary>
/// Gateway → client: session creation rejected. Used for unknown-agent, invalid-agent,
/// and concurrent-session-cap-reached errors so the client can distinguish gateway
/// rejections from ADR-003 error events.
/// </summary>
public sealed record CreateRejectedFrame
{
    [JsonPropertyName("gw")]
    public string Gw => "createRejected";

    /// <summary>
    /// Machine-readable error identifier. Known values:
    /// <c>unknown_agent</c> — the requested agent name does not resolve to a <c>.cs</c>
    ///   composition root under the configured workspace root;
    /// <c>cap_reached</c> — the gateway's concurrent-session limit is exhausted;
    /// <c>core_timeout</c> — the core passed <c>agentReady</c> but did not complete
    /// the create+load handshake within <c>GatewayOptions.CreateHandshakeTimeoutSeconds</c>.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable, actionable message suitable for direct display to the user.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
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
