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
    /// Timeout for the create+load handshake (seconds). After the core passes the
    /// <c>agentReady</c> gate it must emit both <c>session.createResult</c> and
    /// <c>session.loadResult</c> within this window or the just-spawned core is torn
    /// down and the client receives <c>createRejected {code="core_timeout"}</c>.
    /// </summary>
    /// <remarks>
    /// The handshake is a short synchronous exchange (create + load only), so a small
    /// value (30 s default) is intentional. A live-but-silent core that passes
    /// <c>agentReady</c> then stalls on extension load would otherwise park the
    /// connection indefinitely because EOF (crashed core) does NOT cover this case.
    /// </remarks>
    public int CreateHandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Override for the device-key store directory.
    /// When empty or not set, defaults to <c>~/.dmon/gateway/</c> (computed at startup).
    /// The directory must contain <c>devices.json</c> (and will contain <c>lastseen.json</c>).
    /// </summary>
    public string DeviceKeyStoreDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Minimum seconds between last-seen timestamp writes for the same device key (group 7).
    /// Prevents high-frequency writes on busy connections. Defaults to 60.
    /// </summary>
    public int LastSeenThrottleSeconds { get; set; } = 60;

    /// <summary>
    /// Allows binding to a specific non-loopback address (e.g. a Tailscale IP such as
    /// <c>http://100.x.y.z:5500</c>). Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The intended exposure path is <c>tailscale serve</c> fronting the loopback bind
    /// (<c>http://127.0.0.1:5500</c>), not a direct non-loopback bind. Set this only
    /// when you have a specific operational reason and understand the consequences.
    /// Wildcard / all-interfaces addresses (<c>0.0.0.0</c>, <c>::</c>, <c>*</c>, <c>+</c>)
    /// are always rejected regardless of this flag because they expose public NICs.
    /// </remarks>
    public bool AllowNonLoopbackBind { get; set; }
}
