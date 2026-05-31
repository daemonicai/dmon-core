using Dmon.Gateway;
using Dmon.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 9 — Tailscale-fronted auth.
///
/// 9.1 GatewayBindPolicy: loopback-by-default; wildcard always rejected; specific non-loopback
///     needs AllowNonLoopbackBind=true.
/// 9.2 SharedKeyAuthenticator: constant-time bearer-key validation.
/// 9.2 GatewayConnectionEndpoint.HandleAsync: 401 before socket open when key mismatch.
/// </summary>
public sealed class AuthAndBindTests
{
    // -------------------------------------------------------------------------
    // 9.1 — GatewayBindPolicy
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:5500")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://127.1.2.3:5500")]
    public void BindPolicy_Loopback_IPv4_IsAllowed(string address)
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(address, allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_Localhost_IsAllowed()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate("http://localhost:5500", allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_IPv6Loopback_IsAllowed()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate("http://[::1]:5500", allowNonLoopback: false);
        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_LanAddress_Rejected_WhenOptInFalse()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(
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
        (bool allowed, string? error) = GatewayBindPolicy.Validate(
            "http://100.64.0.1:5500", allowNonLoopback: false);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BindPolicy_LanAddress_Allowed_WhenOptInTrue()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(
            "http://192.168.1.10:5500", allowNonLoopback: true);

        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_TailscaleAddress_Allowed_WhenOptInTrue()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(
            "http://100.64.0.1:5500", allowNonLoopback: true);

        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void BindPolicy_IPv4Wildcard_Rejected_EvenWithOptIn()
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(
            "http://0.0.0.0:5500", allowNonLoopback: true);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://[::]:5500")]
    public void BindPolicy_IPv6Wildcard_Rejected_EvenWithOptIn(string address)
    {
        (bool allowed, string? error) = GatewayBindPolicy.Validate(address, allowNonLoopback: true);

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
        (bool allowed, string? error) = GatewayBindPolicy.Validate(address, allowNonLoopback: false);

        Assert.False(allowed);
        Assert.NotNull(error);
        Assert.Contains("tailscale", error, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 9.2 — SharedKeyAuthenticator
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, "Bearer anything")]
    [InlineData("   ", "Bearer anything")]
    public void SharedKey_Disabled_Authorized_Regardless(string? sharedKey, string? header)
    {
        Assert.True(SharedKeyAuthenticator.IsAuthorized(header, sharedKey));
    }

    [Fact]
    public void SharedKey_Disabled_Authorized_WithNoHeader()
    {
        Assert.True(SharedKeyAuthenticator.IsAuthorized(authorizationHeader: null, sharedKey: null));
    }

    [Fact]
    public void SharedKey_Enabled_CorrectBearer_Authorized()
    {
        Assert.True(SharedKeyAuthenticator.IsAuthorized("Bearer s3cr3t", "s3cr3t"));
    }

    [Fact]
    public void SharedKey_Enabled_BearerCaseInsensitive_Authorized()
    {
        Assert.True(SharedKeyAuthenticator.IsAuthorized("bearer s3cr3t", "s3cr3t"));
        Assert.True(SharedKeyAuthenticator.IsAuthorized("BEARER s3cr3t", "s3cr3t"));
    }

    [Fact]
    public void SharedKey_Enabled_WrongKey_NotAuthorized()
    {
        Assert.False(SharedKeyAuthenticator.IsAuthorized("Bearer wrong", "s3cr3t"));
    }

    [Fact]
    public void SharedKey_Enabled_MissingHeader_NotAuthorized()
    {
        Assert.False(SharedKeyAuthenticator.IsAuthorized(authorizationHeader: null, sharedKey: "s3cr3t"));
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("s3cr3t")]
    [InlineData("")]
    [InlineData("Bearer ")]
    public void SharedKey_Enabled_MalformedOrWrongScheme_NotAuthorized(string header)
    {
        Assert.False(SharedKeyAuthenticator.IsAuthorized(header, "s3cr3t"));
    }

    // -------------------------------------------------------------------------
    // 9.2 — HandleAsync: 401 before socket when auth fails
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_MissingHeader_WithSharedKey_Returns401()
    {
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey("s3cr3t");
        DefaultHttpContext context = new();
        // No Authorization header set.

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_WrongKey_WithSharedKey_Returns401()
    {
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey("s3cr3t");
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer wrong-key";

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_CorrectKey_WithSharedKey_PassesAuthCheck()
    {
        // After auth passes, HandleAsync falls through to the IsWebSocketRequest check.
        // DefaultHttpContext has no WebSocket infrastructure so IsWebSocketRequest returns false → 400.
        // The critical assertion is that it is NOT 401 (auth passed).
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey("s3cr3t");
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Bearer s3cr3t";

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_NoSharedKey_NoHeader_PassesAuthCheck()
    {
        // When SharedKey is null the check is disabled; request falls through to 400.
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey(sharedKey: null);
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GatewayConnectionEndpoint MakeEndpointWithKey(string? sharedKey)
    {
        GatewayOptions opts = new() { SharedKey = sharedKey };
        LocalOptionsMonitor optsMon = new(opts);

        return new GatewayConnectionEndpoint(
            new SessionRegistry(),
            optsMon,
            TimeProvider.System,
            NullLogger<GatewayConnectionEndpoint>.Instance);
    }

    private sealed class LocalOptionsMonitor : IOptionsMonitor<GatewayOptions>
    {
        private readonly GatewayOptions _value;

        public LocalOptionsMonitor(GatewayOptions value) => _value = value;

        public GatewayOptions CurrentValue => _value;

        public GatewayOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
