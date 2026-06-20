using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Daemonic.Dmail.Services;

namespace Daemonic.Dmail.Tests;

public class TokenProtectionServiceTests : IDisposable
{
    // Task 11.2: Unit tests for token encryption/decryption round-trip
    private readonly string _keyDir;
    private readonly TokenProtectionService _service;

    public TokenProtectionServiceTests()
    {
        _keyDir = Path.Combine(Path.GetTempPath(), $"dmail-test-keys-{Guid.NewGuid()}");
        Directory.CreateDirectory(_keyDir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(_keyDir))
            .SetApplicationName("dmail-tests");

        var provider = services.BuildServiceProvider();
        var dp = provider.GetRequiredService<IDataProtectionProvider>();
        _service = new TokenProtectionService(dp);
    }

    [Fact]
    public void Protect_Plaintext_ReturnsCiphertext()
    {
        var plaintext = "ya29.a0AfH6..." ; // looks like a Google access token
        var ciphertext = _service.Protect(plaintext);

        Assert.NotNull(ciphertext);
        Assert.NotEmpty(ciphertext);
        Assert.NotEqual(plaintext, ciphertext);
    }

    [Fact]
    public void ProtectAndUnprotect_RoundTrip_ReturnsOriginal()
    {
        var original = "1//0gXyzRefreshToken12345abcdef";
        var protected_ = _service.Protect(original);
        var unprotected = _service.Unprotect(protected_);

        Assert.Equal(original, unprotected);
    }

    [Fact]
    public void Unprotect_InvalidCiphertext_ReturnsNull()
    {
        var result = _service.Unprotect("not-a-valid-ciphertext!!!");

        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_EmptyString_ReturnsEmptyString()
    {
        var result = _service.Unprotect("");

        Assert.Equal("", result);
    }

    [Fact]
    public void Unprotect_Null_ReturnsNull()
    {
        var result = _service.Unprotect(null);

        Assert.Null(result);
    }

    [Fact]
    public void Protect_EmptyString_ReturnsEmptyString()
    {
        var result = _service.Protect("");

        Assert.Equal("", result);
    }

    [Fact]
    public void ProtectAndUnprotect_LongToken_RoundTrip()
    {
        var longToken = new string('x', 500); // 500 chars
        var protected_ = _service.Protect(longToken);
        var unprotected = _service.Unprotect(protected_);

        Assert.Equal(longToken, unprotected);
    }

    [Fact]
    public void ProtectAndUnprotect_SpecialCharacters_RoundTrip()
    {
        var token = "token!@#$%^&*()_+-=[]{}|;':\",./<>?";
        var protected_ = _service.Protect(token);
        var unprotected = _service.Unprotect(protected_);

        Assert.Equal(token, unprotected);
    }

    /// <summary>
    /// Task 3.4: Verify that Unprotect with different key ring returns null
    /// (simulates key ring lost/corrupted scenario).
    /// </summary>
    [Fact]
    public void Unprotect_WithDifferentKeyRing_ReturnsNull()
    {
        // Protect with current key ring
        var ciphertext = _service.Protect("sensitive-token");

        // Create a new service with a different key ring
        var otherKeyDir = Path.Combine(Path.GetTempPath(), $"dmail-test-other-{Guid.NewGuid()}");
        Directory.CreateDirectory(otherKeyDir);

        var otherServices = new ServiceCollection();
        otherServices.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(otherKeyDir))
            .SetApplicationName("dmail-tests-other");

        var otherProvider = otherServices.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        var otherService = new TokenProtectionService(otherProvider);

        var result = otherService.Unprotect(ciphertext);
        Assert.Null(result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_keyDir, recursive: true); } catch { }
    }
}
