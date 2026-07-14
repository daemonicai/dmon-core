using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Dmon.Network;
using Dmon.Network.DeviceKeys;
using Dmon.Network.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Network.Tests;

/// <summary>
/// Auth and bind tests.
///
/// 9.1 NetworkBindPolicy: loopback-by-default; wildcard always rejected; specific non-loopback
///     needs AllowNonLoopbackBind=true.
/// 3.3 DeviceKeyAuthenticator via NetworkConnectionEndpoint.HandleAsync: 401 before socket
///     open when auth fails; empty set authorizes regardless.
/// </summary>
public sealed class AuthAndBindTests
{
    // -------------------------------------------------------------------------
    // 9.1 — NetworkBindPolicy
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:5500")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.1.2.3:5500")]
    public void BindPolicy_Loopback_IPv4_IsAllowed(string address)
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(address, allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_Localhost_IsAllowed()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate("http://localhost:5500", allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_IPv6Loopback_IsAllowed()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate("http://[::1]:5500", allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_LanAddress_Rejected_WhenOptInFalse()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(
            "http://192.168.1.10:5500", allowNonLoopback: false);

        Assert.False(allowed);
        Assert.NotNull(error);
        // Error must be actionable: mention tailscale and the opt-in flag.
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowNonLoopbackBind", error);
    }

    [Fact]
    public void BindPolicy_TailscaleAddress_Rejected_WhenOptInFalse()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(
            "http://100.64.0.1:5500", allowNonLoopback: false);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindPolicy_LanAddress_Allowed_WhenOptInTrue()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(
            "http://192.168.1.10:5500", allowNonLoopback: true);

        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_TailscaleAddress_Allowed_WhenOptInTrue()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(
            "http://100.64.0.1:5500", allowNonLoopback: true);

        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_IPv4Wildcard_Rejected_EvenWithOptIn()
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(
            "http://0.0.0.0:5500", allowNonLoopback: true);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://[::]:5500")]
    public void BindPolicy_IPv6Wildcard_Rejected_EvenWithOptIn(string address)
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(address, allowNonLoopback: true);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://garbage@@:xyz/path")]
    public void BindPolicy_EmptyOrGarbage_Rejected(string address)
    {
        (bool allowed, string? error) = NetworkBindPolicy.Validate(address, allowNonLoopback: false);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 3.3 — HandleAsync: 401 before socket when device-key auth fails
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_EmptyKeySet_NoHeader_PassesAuthCheck()
    {
        // Empty set → auth disabled; falls through to IsWebSocketRequest check (400, not 401).
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty);
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_EmptyKeySet_WithHeader_PassesAuthCheck()
    {
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty);
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer anything";

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_MissingHeader_Returns401()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set);
        DefaultHttpContext context = new();
        // No Authorization header.

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_WrongToken_Returns401()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set);
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_CorrectToken_PassesAuthCheck()
    {
        // Auth passes → falls through to IsWebSocketRequest (400 non-WS context, not 401).
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set);
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer s3cr3t";

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("s3cr3t")]
    [InlineData("")]
    [InlineData("Bearer ")]
    public async Task HandleAsync_NonEmptySet_MalformedOrWrongScheme_Returns401(string header)
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set);
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = header;

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 2.x — HandleAsync: empty key set fails closed on a non-loopback bind (ADR-036)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_EmptyKeySet_NonLoopbackBind_NoHeader_Returns401()
    {
        // Empty set + non-loopback effective bind → fail closed before any socket.
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty, NonLoopbackOptions());
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_EmptyKeySet_NonLoopbackBind_WithHeader_Returns401()
    {
        // An Authorization header must not rescue the empty-set non-loopback path.
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty, NonLoopbackOptions());
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer anything";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_EmptyKeySet_LoopbackBind_PassesAuthCheck()
    {
        // Empty set + loopback bind → auth disabled, unchanged from before ADR-036.
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty);
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_NonLoopbackBind_CorrectToken_PassesAuthCheck()
    {
        // Non-empty set enforces per-device Bearer identically on a non-loopback bind.
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set, NonLoopbackOptions());
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer s3cr3t";

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_NonLoopbackBind_MissingHeader_Returns401()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set, NonLoopbackOptions());
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NonEmptySet_NonLoopbackBind_WrongToken_Returns401()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(set, NonLoopbackOptions());
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 3.2 — Origin allowlist on the /ws upgrade (ADR-036 Decision 2)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_NoOriginHeader_ProceedsToAuth()
    {
        // (a) No Origin header ⇒ native client ⇒ proceed (falls through to IsWebSocketRequest 400).
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty);
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_OriginPresent_EmptyAllowlist_Returns403()
    {
        // (b) Origin present + default empty allowlist ⇒ 403 before any socket.
        NetworkConnectionEndpoint endpoint = MakeEndpoint(DeviceKeySet.Empty);
        DefaultHttpContext context = new();
        context.Request.Headers.Origin = "https://app.example";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_OriginPresent_ExactMatch_ProceedsToAuth()
    {
        // (c) Origin present + exact allowlist match ⇒ proceed (falls through to 400).
        NetworkConnectionEndpoint endpoint = MakeEndpoint(
            DeviceKeySet.Empty,
            new NetworkOptions { AllowedOrigins = ["https://app.example"] });
        DefaultHttpContext context = new();
        context.Request.Headers.Origin = "https://app.example";

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_AllowlistedOrigin_BadToken_Still401()
    {
        // (d) Independence: allowlisted origin does NOT short-circuit past auth. A non-empty key set
        // with a bad token still 401s. Loopback default bind, so the empty-set 401 gate never fires
        // and control cleanly reaches the device-key auth 401.
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));
        NetworkConnectionEndpoint endpoint = MakeEndpoint(
            set,
            new NetworkOptions { AllowedOrigins = ["https://app.example"] });
        DefaultHttpContext context = new();
        context.Request.Headers.Origin = "https://app.example";
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>A non-loopback effective bind: specific host + explicit opt-in.</summary>
    private static NetworkOptions NonLoopbackOptions() =>
        new() { BindAddress = "http://100.64.0.1:5500", AllowNonLoopbackBind = true };

    /// <summary>Returns the lowercase-hex SHA-256 of <paramref name="token"/> (the stored form).</summary>
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static DeviceKeySet MakeSet(params (string keyId, string token)[] entries)
    {
        ImmutableArray<DeviceCredential>.Builder builder = ImmutableArray.CreateBuilder<DeviceCredential>();
        foreach ((string keyId, string token) in entries)
        {
            builder.Add(new DeviceCredential(
                KeyId: keyId,
                Name: keyId,
                SecretHash: HashToken(token),
                CreatedAt: DateTimeOffset.UtcNow,
                RevokedAt: null));
        }

        return new DeviceKeySet(builder.ToImmutable());
    }

    private static NetworkConnectionEndpoint MakeEndpoint(DeviceKeySet keySet) =>
        new(new SessionRegistry(),
            new NetworkConnectionEndpoint.TestOptions
            {
                DeviceKeySetProvider = new DeviceKeySetProvider(keySet),
            },
            NullLogger<NetworkConnectionEndpoint>.Instance);

    private static NetworkConnectionEndpoint MakeEndpoint(DeviceKeySet keySet, NetworkOptions options) =>
        new(new SessionRegistry(),
            new NetworkConnectionEndpoint.TestOptions
            {
                DeviceKeySetProvider = new DeviceKeySetProvider(keySet),
                Options = new NetworkConnectionEndpoint.StaticOptionsMonitor(options),
            },
            NullLogger<NetworkConnectionEndpoint>.Instance);
}
