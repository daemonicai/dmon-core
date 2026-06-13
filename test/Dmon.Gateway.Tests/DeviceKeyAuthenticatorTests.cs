using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Dmon.Gateway.DeviceKeys;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 2 — DeviceKeyAuthenticator (tasks 2.1–2.3).
/// </summary>
public sealed class DeviceKeyAuthenticatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // 2.2 — empty set short-circuits to authorized (no keyId)
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptySet_AuthorizedRegardlessOfHeader()
    {
        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate(
            authorizationHeader: null,
            keySet: DeviceKeySet.Empty);

        Assert.True(result.Authorized);
        Assert.Null(result.KeyId);
    }

    [Fact]
    public void EmptySet_AuthorizedWithPresentHeader()
    {
        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate(
            authorizationHeader: "Bearer sometoken",
            keySet: DeviceKeySet.Empty);

        Assert.True(result.Authorized);
        Assert.Null(result.KeyId);
    }

    // -------------------------------------------------------------------------
    // 2.1 — missing / malformed header rejected when set is non-empty
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NonEmptySet_MissingHeader_NotAuthorized(string? header)
    {
        DeviceKeySet set = MakeSet(("k1", "secret"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate(header, set);

        Assert.False(result.Authorized);
        Assert.Null(result.KeyId);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]   // wrong scheme
    [InlineData("secret")]               // bare token, no scheme
    [InlineData("Bearer ")]              // empty token after scheme
    [InlineData("bearer")]               // scheme word only, no space → no token (distinct from the case-insensitive success cases which supply a token)
    public void NonEmptySet_MalformedOrWrongScheme_NotAuthorized(string header)
    {
        DeviceKeySet set = MakeSet(("k1", "secret"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate(header, set);

        Assert.False(result.Authorized);
    }

    // -------------------------------------------------------------------------
    // 2.1 — Bearer scheme is case-insensitive (RFC 7235)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("bEaReR")]
    public void NonEmptySet_BearerSchemeCaseInsensitive_Authorized(string scheme)
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate($"{scheme} s3cr3t", set);

        Assert.True(result.Authorized);
        Assert.Equal("k1", result.KeyId);
    }

    // -------------------------------------------------------------------------
    // 2.1 — correct token matches and returns keyId
    // -------------------------------------------------------------------------

    [Fact]
    public void NonEmptySet_CorrectToken_AuthorizedWithKeyId()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate("Bearer s3cr3t", set);

        Assert.True(result.Authorized);
        Assert.Equal("k1", result.KeyId);
    }

    [Fact]
    public void NonEmptySet_WrongToken_NotAuthorized()
    {
        DeviceKeySet set = MakeSet(("k1", "s3cr3t"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate("Bearer wrong", set);

        Assert.False(result.Authorized);
        Assert.Null(result.KeyId);
    }

    // -------------------------------------------------------------------------
    // 2.1 — multiple entries: correct entry's keyId returned
    // -------------------------------------------------------------------------

    [Fact]
    public void MultipleEntries_MatchesCorrectKeyId()
    {
        DeviceKeySet set = MakeSet(("k1", "token-one"), ("k2", "token-two"), ("k3", "token-three"));

        DeviceAuthResult r1 = DeviceKeyAuthenticator.Authenticate("Bearer token-two", set);

        Assert.True(r1.Authorized);
        Assert.Equal("k2", r1.KeyId);
    }

    [Fact]
    public void MultipleEntries_NoMatch_NotAuthorized()
    {
        DeviceKeySet set = MakeSet(("k1", "token-one"), ("k2", "token-two"));

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate("Bearer unknown", set);

        Assert.False(result.Authorized);
        Assert.Null(result.KeyId);
    }

    // -------------------------------------------------------------------------
    // Malformed-hex guard — bad row skipped, valid row still matches
    // -------------------------------------------------------------------------

    [Fact]
    public void MalformedHexRow_Skipped_ValidRowStillMatches()
    {
        // Build a set manually: one entry has a non-hex secretHash, one is valid.
        ImmutableArray<DeviceCredential> entries = ImmutableArray.Create(
            new DeviceCredential("k-bad", "bad-entry", "ZZZNOTVALIDHEX", DateTimeOffset.UtcNow, null),
            new DeviceCredential("k-good", "good-entry", HashToken("good-token"), DateTimeOffset.UtcNow, null));

        DeviceKeySet set = new(entries);

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate("Bearer good-token", set);

        Assert.True(result.Authorized);
        Assert.Equal("k-good", result.KeyId);
    }

    [Fact]
    public void MalformedHexRow_OnlyBadRows_NotAuthorized()
    {
        ImmutableArray<DeviceCredential> entries = ImmutableArray.Create(
            new DeviceCredential("k-bad", "bad-entry", "ZZZNOTVALIDHEX", DateTimeOffset.UtcNow, null));

        DeviceKeySet set = new(entries);

        DeviceAuthResult result = DeviceKeyAuthenticator.Authenticate("Bearer anything", set);

        Assert.False(result.Authorized);
    }

    // -------------------------------------------------------------------------
    // Result sentinel values
    // -------------------------------------------------------------------------

    [Fact]
    public void AuthorizedNoKey_HasNoKeyId()
    {
        Assert.True(DeviceAuthResult.AuthorizedNoKey.Authorized);
        Assert.Null(DeviceAuthResult.AuthorizedNoKey.KeyId);
    }

    [Fact]
    public void NotAuthorized_IsNotAuthorized()
    {
        Assert.False(DeviceAuthResult.NotAuthorized.Authorized);
        Assert.Null(DeviceAuthResult.NotAuthorized.KeyId);
    }
}
