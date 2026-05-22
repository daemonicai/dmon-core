using Daemon.Core.Auth;

namespace Daemon.Core.Tests.Auth;

public sealed class CredentialFileStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CredentialFileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"daemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        CredentialFileStore store = new(_tempDir);

        CredentialRecord? result = await store.ReadAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_RoundTrip_PreservesAllFields()
    {
        CredentialFileStore store = new(_tempDir);
        DateTimeOffset now = new(2026, 5, 22, 10, 30, 0, TimeSpan.Zero);

        CredentialRecord written = new()
        {
            Provider = "anthropic",
            CredentialType = "apiKey",
            ApiKey = "sk-ant-api03-testkey123",
            HeaderStyle = "x-api-key",
            CreatedAt = now,
            UpdatedAt = now
        };

        await store.WriteAsync(written);
        CredentialRecord? read = await store.ReadAsync("anthropic");

        Assert.NotNull(read);
        Assert.Equal("anthropic", read!.Provider);
        Assert.Equal("apiKey", read.CredentialType);
        Assert.Equal("sk-ant-api03-testkey123", read.ApiKey);
        Assert.Equal("x-api-key", read.HeaderStyle);
        Assert.Equal(now, read.CreatedAt);
        Assert.Equal(now, read.UpdatedAt);
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        CredentialFileStore store = new(_tempDir);

        CredentialRecord first = new()
        {
            Provider = "openai",
            CredentialType = "apiKey",
            ApiKey = "key-one",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.WriteAsync(first);

        CredentialRecord second = new()
        {
            Provider = "openai",
            CredentialType = "apiKey",
            ApiKey = "key-two",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.WriteAsync(second);

        CredentialRecord? read = await store.ReadAsync("openai");
        Assert.NotNull(read);
        Assert.Equal("key-two", read!.ApiKey);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile()
    {
        CredentialFileStore store = new(_tempDir);

        await store.WriteAsync(new CredentialRecord
        {
            Provider = "gemini",
            CredentialType = "apiKey",
            ApiKey = "gemini-key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await store.DeleteAsync("gemini");

        CredentialRecord? result = await store.ReadAsync("gemini");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        CredentialFileStore store = new(_tempDir);

        // Should not throw.
        await store.DeleteAsync("nonexistent");
    }

    [Fact]
    public async Task ReadAsync_SanitisesFileNameWithInvalidChars()
    {
        CredentialFileStore store = new(_tempDir);

        await store.WriteAsync(new CredentialRecord
        {
            Provider = "provider/with:invalid",
            CredentialType = "apiKey",
            ApiKey = "key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Reading back with the same provider name uses the same sanitisation.
        CredentialRecord? result = await store.ReadAsync("provider/with:invalid");
        Assert.NotNull(result);
        Assert.Equal("key", result!.ApiKey);
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectory_WhenItDoesNotExist()
    {
        string nestedDir = Path.Combine(_tempDir, "nested", "credentials");
        CredentialFileStore store = new(nestedDir);

        await store.WriteAsync(new CredentialRecord
        {
            Provider = "test",
            CredentialType = "apiKey",
            ApiKey = "key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        Assert.True(Directory.Exists(nestedDir));
    }
}
