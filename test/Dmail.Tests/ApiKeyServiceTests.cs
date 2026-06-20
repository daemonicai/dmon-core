using Dmail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmail.Tests;

public class ApiKeyServiceTests
{
    // B5: constant-time API key validation via FixedTimeEquals.
    private static ApiKeyService Build(string key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DMAIL_API_KEY"] = key })
            .Build();
        return new ApiKeyService(config, NullLogger<ApiKeyService>.Instance);
    }

    [Fact]
    public void Validate_CorrectKey_ReturnsTrue()
    {
        var service = Build("super-secret-key");
        Assert.True(service.Validate("super-secret-key"));
    }

    [Fact]
    public void Validate_WrongKeySameLength_ReturnsFalse()
    {
        var service = Build("super-secret-key");
        Assert.False(service.Validate("super-secret-xxx"));
    }

    [Fact]
    public void Validate_WrongKeyDifferentLength_ReturnsFalse()
    {
        var service = Build("super-secret-key");
        Assert.False(service.Validate("short"));
        Assert.False(service.Validate("super-secret-key-with-extra-bytes"));
    }

    [Fact]
    public void Validate_NullOrEmpty_ReturnsFalse()
    {
        var service = Build("super-secret-key");
        Assert.False(service.Validate(null));
        Assert.False(service.Validate(""));
    }

    [Fact]
    public void Validate_NonAsciiKey_RoundTrips()
    {
        var service = Build("clé-secrète-🔑");
        Assert.True(service.Validate("clé-secrète-🔑"));
        Assert.False(service.Validate("clé-secrète-x"));
    }
}
