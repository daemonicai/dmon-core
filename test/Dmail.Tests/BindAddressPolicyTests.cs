namespace Dmail.Tests;

public class BindAddressPolicyTests
{
    [Fact]
    public void Resolve_NoBindAddress_DefaultsToLoopback()
    {
        var resolved = BindAddressPolicy.Resolve(null, "8080", allowNonLoopback: false);

        Assert.Equal("http://127.0.0.1:8080", resolved);
    }

    [Fact]
    public void Resolve_EmptyBindAddress_DefaultsToLoopback()
    {
        var resolved = BindAddressPolicy.Resolve(string.Empty, "8080", allowNonLoopback: false);

        Assert.Equal("http://127.0.0.1:8080", resolved);
    }

    [Fact]
    public void Resolve_WildcardWithoutOptIn_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://+:8080", "8080", allowNonLoopback: false));

        Assert.Contains("http://+:8080", ex.Message);
        Assert.Contains("DMAIL_ALLOW_NONLOOPBACK", ex.Message);
    }

    [Fact]
    public void Resolve_WildcardWithOptIn_IsAccepted()
    {
        var resolved = BindAddressPolicy.Resolve("http://+:8080", "8080", allowNonLoopback: true);

        Assert.Equal("http://+:8080", resolved);
    }

    [Fact]
    public void Resolve_AllInterfacesIPv4WithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://0.0.0.0:8080", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_AllInterfacesIPv6WithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://[::]:8080", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_SpecificNonLoopbackWithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://192.168.1.10:8080", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_SpecificNonLoopbackWithOptIn_IsAccepted()
    {
        var resolved = BindAddressPolicy.Resolve("http://192.168.1.10:8080", "8080", allowNonLoopback: true);

        Assert.Equal("http://192.168.1.10:8080", resolved);
    }

    [Fact]
    public void Resolve_ExplicitLoopback_IsAcceptedWithoutOptIn()
    {
        var resolved = BindAddressPolicy.Resolve("http://127.0.0.1:9090", "8080", allowNonLoopback: false);

        Assert.Equal("http://127.0.0.1:9090", resolved);
    }

    [Fact]
    public void Resolve_LocalhostHostname_IsAcceptedWithoutOptIn()
    {
        var resolved = BindAddressPolicy.Resolve("http://localhost:9090", "8080", allowNonLoopback: false);

        Assert.Equal("http://localhost:9090", resolved);
    }

    [Fact]
    public void Resolve_IPv6LoopbackBracketed_IsAcceptedWithoutOptIn()
    {
        var resolved = BindAddressPolicy.Resolve("http://[::1]:9090", "8080", allowNonLoopback: false);

        Assert.Equal("http://[::1]:9090", resolved);
    }

    [Fact]
    public void Resolve_InvalidUrl_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("not-a-url", "8080", allowNonLoopback: false));

        Assert.Contains("not-a-url", ex.Message);
    }

    [Fact]
    public void Resolve_WildcardNoPortWithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://+", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_LoopbackNoPortWithoutOptIn_IsAccepted()
    {
        var resolved = BindAddressPolicy.Resolve("http://127.0.0.1", "8080", allowNonLoopback: false);

        Assert.Equal("http://127.0.0.1", resolved);
    }

    [Fact]
    public void Resolve_WildcardWithUserinfoWithoutOptIn_Throws()
    {
        // The host must not be extracted as a bare "+" by stripping the userinfo —
        // it should still be rejected (as a non-loopback host either way).
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://user@+:8080", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_UppercaseLocalhostWithoutOptIn_IsAccepted()
    {
        var resolved = BindAddressPolicy.Resolve("http://LOCALHOST:8080", "8080", allowNonLoopback: false);

        Assert.Equal("http://LOCALHOST:8080", resolved);
    }

    [Fact]
    public void Resolve_DnsHostnameWithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://example.com:8080", "8080", allowNonLoopback: false));
    }

    [Fact]
    public void Resolve_DnsHostnameWithOptIn_IsAccepted()
    {
        var resolved = BindAddressPolicy.Resolve("http://example.com:8080", "8080", allowNonLoopback: true);

        Assert.Equal("http://example.com:8080", resolved);
    }

    [Fact]
    public void Resolve_WildcardIPv6WithTrailingPathWithoutOptIn_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BindAddressPolicy.Resolve("http://[::]:8080/foo", "8080", allowNonLoopback: false));
    }
}
