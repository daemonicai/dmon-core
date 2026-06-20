using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dmail.Services;

public sealed class OAuth2Service
{
    private readonly IConfiguration _config;
    private readonly AccountService _accounts;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<OAuth2Service> _logger;

    private const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";

    public OAuth2Service(
        IConfiguration config,
        AccountService accounts,
        IHttpClientFactory http,
        ILogger<OAuth2Service> logger)
    {
        _config = config;
        _accounts = accounts;
        _http = http;
        _logger = logger;
    }

    public (string AuthUrl, string CodeVerifier, string State) BuildAuthorizationUrl(string redirectUri)
    {
        var clientId = _config["DMAIL_GOOGLE_CLIENT_ID"]
            ?? throw new InvalidOperationException("DMAIL_GOOGLE_CLIENT_ID not configured");

        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateState();

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "https://mail.google.com/",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        };

        var authUrl = $"{GoogleAuthUrl}?{string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))}";
        return (authUrl, codeVerifier, state);
    }

    public async Task<string> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri,
        CancellationToken ct = default)
    {
        var clientId = _config["DMAIL_GOOGLE_CLIENT_ID"]
            ?? throw new InvalidOperationException("DMAIL_GOOGLE_CLIENT_ID not configured");
        var clientSecret = _config["DMAIL_GOOGLE_CLIENT_SECRET"]
            ?? throw new InvalidOperationException("DMAIL_GOOGLE_CLIENT_SECRET not configured");

        var client = _http.CreateClient("google");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        var response = await client.PostAsync(GoogleTokenUrl, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Google token exchange failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Google token exchange failed with {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var accessToken = json.GetProperty("access_token").GetString()!;

        // Google omits refresh_token when consent was previously granted without
        // prompt=consent. Without it we cannot keep the account connected, so surface
        // this as an auth error (the callback maps InvalidOperationException to
        // /?auth=error) rather than throwing KeyNotFoundException as a 500.
        if (!json.TryGetProperty("refresh_token", out var refreshTokenElement) ||
            refreshTokenElement.GetString() is not { Length: > 0 } refreshToken)
        {
            _logger.LogWarning("OAuth2 token response did not include a refresh_token");
            throw new InvalidOperationException("Google did not return a refresh token");
        }

        var expiresIn = json.GetProperty("expires_in").GetInt32();
        var expiry = DateTime.UtcNow.AddSeconds(expiresIn);

        // Get email from token info
        var email = await GetEmailFromTokenAsync(accessToken, ct);

        await _accounts.StoreTokensAsync(email, accessToken, refreshToken, expiry);

        _logger.LogInformation("OAuth2 flow complete for {Email}", email);
        return email;
    }

    /// <summary>
    /// Task 5.2: Refresh access token when within 5 minutes of expiry.
    /// Returns the new access token, or null if refresh fails.
    /// </summary>
    public async Task<(string? AccessToken, DateTime? Expiry)> RefreshTokenAsync(
        string email, string refreshToken, CancellationToken ct = default)
    {
        var clientId = _config["DMAIL_GOOGLE_CLIENT_ID"]
            ?? throw new InvalidOperationException("DMAIL_GOOGLE_CLIENT_ID not configured");
        var clientSecret = _config["DMAIL_GOOGLE_CLIENT_SECRET"];

        var client = _http.CreateClient("google");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret ?? "",
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        try
        {
            var response = await client.PostAsync(GoogleTokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var accessToken = json.GetProperty("access_token").GetString()!;
            var expiresIn = json.GetProperty("expires_in").GetInt32();
            var expiry = DateTime.UtcNow.AddSeconds(expiresIn);

            // Store updated tokens
            await _accounts.StoreTokensAsync(email, accessToken, refreshToken, expiry);

            _logger.LogInformation("Token refreshed for {Email}, expires {Expiry}", email, expiry);
            return (accessToken, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed for {Email}", email);
            return (null, null);
        }
    }

    /// <summary>
    /// Task 5.2: Check if token needs refresh (within 5 minutes of expiry).
    /// </summary>
    public static bool NeedsRefresh(DateTime? expiry)
    {
        return expiry == null || DateTime.UtcNow.AddMinutes(5) >= expiry.Value;
    }

    private async Task<string> GetEmailFromTokenAsync(string accessToken, CancellationToken ct)
    {
        var client = _http.CreateClient("google");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(
            "https://www.googleapis.com/gmail/v1/users/me/profile", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gmail profile lookup failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Gmail profile lookup failed with {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("emailAddress").GetString()!;
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
