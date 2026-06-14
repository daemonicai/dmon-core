using System.Security.Cryptography;
using System.Text;

namespace Daemonic.Dmail.Services;

public sealed class ApiKeyService
{
    private readonly string _apiKey;
    private readonly ILogger<ApiKeyService> _logger;

    public string ApiKey => _apiKey;

    public ApiKeyService(IConfiguration config, ILogger<ApiKeyService> logger)
    {
        _logger = logger;
        var configuredKey = config["DMAIL_API_KEY"];

        if (!string.IsNullOrEmpty(configuredKey))
        {
            _apiKey = configuredKey;
            _logger.LogInformation("Using configured API key");
        }
        else
        {
            // Task 8.1: Auto-generate on first run
            _apiKey = GenerateApiKey();
            _logger.LogWarning("Auto-generated API key: {Key}", _apiKey);
            _logger.LogWarning("Set DMAIL_API_KEY environment variable to use a fixed key");
        }
    }

    public bool Validate(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        // Constant-time comparison over UTF-8 bytes. FixedTimeEquals returns false
        // for differing lengths without an early-out timing side-channel.
        var expected = Encoding.UTF8.GetBytes(_apiKey);
        var provided = Encoding.UTF8.GetBytes(key);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
