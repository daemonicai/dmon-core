using System.Security.Cryptography;
using System.Text;

namespace Dcal;

internal sealed class ApiKeyService
{
    private readonly string _apiKey;
    private readonly ILogger<ApiKeyService> _logger;

    public string ApiKey => _apiKey;

    public ApiKeyService(IConfiguration config, ILogger<ApiKeyService> logger)
    {
        _logger = logger;
        var configuredKey = config["DCAL_API_KEY"];

        if (!string.IsNullOrEmpty(configuredKey))
        {
            _apiKey = configuredKey;
            _logger.LogInformation("Using configured API key");
        }
        else
        {
            // Dcal is bare-metal-first (no Dockerfile; calendar.db already lives in the
            // working directory), unlike Dmail's Docker-first "/data" default — so the
            // default here is "." to keep a bare `dotnet run` able to write the key file.
            var dataDir = config["DCAL_DATA_DIR"] ?? ".";
            var path = Path.Combine(dataDir, "keys", "api-key");

            if (File.Exists(path))
            {
                _apiKey = File.ReadAllText(path).Trim();
            }
            else
            {
                _apiKey = GenerateApiKey();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteKeyFile(path, _apiKey);

                _logger.LogInformation(
                    "Auto-generated API key written to {Path}. Set DCAL_API_KEY to override.",
                    path);
            }
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

    private static void WriteKeyFile(string path, string key)
    {
        if (!OperatingSystem.IsWindows())
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.Write(key);
        }
        else
        {
            File.WriteAllText(path, key);
        }
    }
}
