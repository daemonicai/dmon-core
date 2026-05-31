namespace Dmon.Gateway;

/// <summary>
/// Configuration for the remote-session gateway.
/// Bind via <c>IConfiguration.GetSection("Gateway").Bind(...)</c> or
/// <c>services.Configure&lt;GatewayOptions&gt;(config.GetSection("Gateway"))</c>.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>
    /// The configuration section name used when binding from <c>appsettings.json</c>.
    /// </summary>
    public const string SectionName = "Gateway";

    /// <summary>
    /// Kestrel listen address. Defaults to loopback on port 5500 (D5 — never a public NIC by default).
    /// Override in config to expose via <c>tailscale serve</c>.
    /// </summary>
    public string BindAddress { get; set; } = "http://127.0.0.1:5500";

    /// <summary>
    /// How long a session handler survives with no connected client and no in-flight turn (minutes).
    /// </summary>
    public int IdleDetachedTtlMinutes { get; set; } = 15;

    /// <summary>
    /// Absolute upper bound on how long a session handler runs while a turn is in flight,
    /// regardless of attachment state (minutes).
    /// </summary>
    public int RunningTurnTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Interval between gateway→client heartbeat ping frames (seconds).
    /// Keeps connections alive across carrier-NAT idle timeouts and provides
    /// the liveness signal for missed-heartbeat disconnect detection.
    /// A missed-beat deadline of 2× the interval is used: if no frame arrives
    /// from the client within that window the connection is treated as dead
    /// and the forwarding loop exits (starting the detached grace timer).
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 25;

    /// <summary>
    /// Maximum number of concurrently active session handlers.
    /// New attach requests are rejected with 503 when the cap is reached.
    /// </summary>
    public int MaxConcurrentHandlers { get; set; } = 10;

    /// <summary>
    /// Optional pre-shared key for defense-in-depth (D5 / group 9).
    /// When <see langword="null"/> or empty, the shared-key check is disabled.
    /// Validation logic is wired in group 9.
    /// </summary>
    public string? SharedKey { get; set; }
}
