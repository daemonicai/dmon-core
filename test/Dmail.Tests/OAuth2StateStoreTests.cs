using Dmail.Services;

namespace Dmail.Tests;

public class OAuth2StateStoreTests
{
    [Fact]
    public void GetVerifier_KnownState_ReturnsStoredVerifier()
    {
        var store = new OAuth2StateStore();
        store.Store("state-1", "verifier-1");

        Assert.Equal("verifier-1", store.GetVerifier("state-1"));
    }

    [Fact]
    public void GetVerifier_UnknownState_ReturnsNull()
    {
        var store = new OAuth2StateStore();

        Assert.Null(store.GetVerifier("never-stored"));
    }

    [Fact]
    public void GetVerifier_IsSingleUse_SecondLookupReturnsNull()
    {
        var store = new OAuth2StateStore();
        store.Store("state-1", "verifier-1");

        // First lookup consumes the state (TryRemove); a replayed state must not
        // resolve a verifier, so the callback cannot re-exchange the code.
        Assert.Equal("verifier-1", store.GetVerifier("state-1"));
        Assert.Null(store.GetVerifier("state-1"));
    }
}
