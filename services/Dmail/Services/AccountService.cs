using Dmail.Data;

namespace Dmail.Services;

public sealed class AccountService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TokenProtectionService _tokenProtection;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        ISqliteConnectionFactory connectionFactory,
        TokenProtectionService tokenProtection,
        ILogger<AccountService> logger)
    {
        _connectionFactory = connectionFactory;
        _tokenProtection = tokenProtection;
        _logger = logger;
    }

    public async Task StoreTokensAsync(string email, string accessToken, string refreshToken, DateTime? expiry)
    {
        var encryptedAccess = _tokenProtection.Protect(accessToken);
        var encryptedRefresh = _tokenProtection.Protect(refreshToken);

        await using var connection = await _connectionFactory.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO accounts (email, access_token_encrypted, refresh_token_encrypted, token_expiry, account_state)
            VALUES (@email, @access, @refresh, @expiry, 'connected')
            ON CONFLICT(email) DO UPDATE SET
                access_token_encrypted = excluded.access_token_encrypted,
                refresh_token_encrypted = excluded.refresh_token_encrypted,
                token_expiry = excluded.token_expiry,
                account_state = 'connected'";
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@access", encryptedAccess);
        cmd.Parameters.AddWithValue("@refresh", encryptedRefresh);
        cmd.Parameters.AddWithValue("@expiry", expiry?.ToString("O") ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(string? AccessToken, string? RefreshToken, DateTime? Expiry)> GetTokensAsync(string email)
    {
        await using var connection = await _connectionFactory.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT access_token_encrypted, refresh_token_encrypted, token_expiry FROM accounts WHERE email = @email";
        cmd.Parameters.AddWithValue("@email", email);

        string? encryptedAccess;
        string? encryptedRefresh;
        string? expiryStr;
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return (null, null, null);

            encryptedAccess = reader.IsDBNull(0) ? null : reader.GetString(0);
            encryptedRefresh = reader.IsDBNull(1) ? null : reader.GetString(1);
            expiryStr = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var accessToken = _tokenProtection.Unprotect(encryptedAccess);
        var refreshToken = _tokenProtection.Unprotect(encryptedRefresh);

        // Task 3.4: If decryption fails, mark account as needing re-auth
        if ((!string.IsNullOrEmpty(encryptedAccess) && accessToken == null) ||
            (!string.IsNullOrEmpty(encryptedRefresh) && refreshToken == null))
        {
            _logger.LogWarning("Token decryption failed for {Email} — requiring re-authentication", email);
            await MarkReAuthenticationRequiredAsync(connection, email);
            return (null, null, null);
        }

        DateTime? expiry = expiryStr != null ? DateTime.Parse(expiryStr) : null;
        return (accessToken, refreshToken, expiry);
    }

    private static async Task MarkReAuthenticationRequiredAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string email)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET account_state = 'reauthentication_required' WHERE email = @email";
        cmd.Parameters.AddWithValue("@email", email);
        await cmd.ExecuteNonQueryAsync();
    }
}
