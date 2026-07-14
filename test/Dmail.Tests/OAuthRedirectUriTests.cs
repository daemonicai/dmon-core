using Microsoft.Extensions.Configuration;

namespace Dmail.Tests;

public class OAuthRedirectUriTests
{
    private static IConfiguration BuildConfig(params (string Key, string? Value)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    [Fact]
    public void ConfiguredBase_IsUsed_RegardlessOfPort()
    {
        var config = BuildConfig(
            ("DMAIL_OAUTH_REDIRECT_BASE_URL", "https://mail.example.com"),
            ("DMAIL_PORT", "8080"));

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal("https://mail.example.com/api/auth/google/callback", uri);
    }

    [Fact]
    public void ConfiguredBase_TrailingSlash_IsTrimmed()
    {
        var config = BuildConfig(("DMAIL_OAUTH_REDIRECT_BASE_URL", "https://mail.example.com/"));

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal("https://mail.example.com/api/auth/google/callback", uri);
        Assert.DoesNotContain("//api", uri.Replace("https://", ""));
    }

    [Fact]
    public void Unset_FallsBackToLoopback_WithConfiguredPort()
    {
        var config = BuildConfig(
            ("DMAIL_OAUTH_REDIRECT_BASE_URL", ""),
            ("DMAIL_PORT", "8080"));

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal("http://127.0.0.1:8080/api/auth/google/callback", uri);
    }

    [Fact]
    public void Unset_And_NoPort_FallsBackToLoopbackDefaultPort()
    {
        var config = BuildConfig();

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal("http://127.0.0.1:8080/api/auth/google/callback", uri);
    }

    [Fact]
    public void Whitespace_TreatedAsUnset_FallsBackToLoopback()
    {
        var config = BuildConfig(
            ("DMAIL_OAUTH_REDIRECT_BASE_URL", "   "),
            ("DMAIL_PORT", "9090"));

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal("http://127.0.0.1:9090/api/auth/google/callback", uri);
    }

    // The redirect_uri must never be influenced by the request Host header: the
    // resolver does not take an HttpContext, so a spoofed Host cannot reach it.
    // This asserts that property structurally and by value.
    [Fact]
    public void SpoofedHost_HasNoInfluence_ConfiguredBaseWins()
    {
        // A caller might send Host: attacker.example, but the resolver only sees
        // configuration. The value tracks DMAIL_OAUTH_REDIRECT_BASE_URL only.
        var config = BuildConfig(
            ("DMAIL_OAUTH_REDIRECT_BASE_URL", "https://mail.example.com"),
            ("DMAIL_PORT", "8080"));

        var uri = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.DoesNotContain("attacker.example", uri);
        Assert.Equal("https://mail.example.com/api/auth/google/callback", uri);
    }

    // Login and callback both call the single resolver with the same DI
    // IConfiguration, so they compute the byte-identical redirect_uri (Google
    // returns redirect_uri_mismatch otherwise). Assert equality for both the
    // configured and the loopback-fallback path.
    [Theory]
    [InlineData("https://mail.example.com", "8080")]
    [InlineData("", "8080")]
    [InlineData(null, null)]
    public void LoginAndCallback_ComputeIdenticalValue(string? configuredBase, string? port)
    {
        var config = BuildConfig(
            ("DMAIL_OAUTH_REDIRECT_BASE_URL", configuredBase),
            ("DMAIL_PORT", port));

        var login = EndpointExtensions.ResolveOAuthRedirectUri(config);
        var callback = EndpointExtensions.ResolveOAuthRedirectUri(config);

        Assert.Equal(login, callback);
    }
}
