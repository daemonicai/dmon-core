using Dmon.Abstractions.Providers;
using Dmon.Core.Auth;
using Dmon.Core.Providers;

namespace Dmon.Core.Tests.Auth;

public sealed class AuthServiceTests
{
    private const string TestEnvVar = "DAEMON_TEST_AUTH_SERVICE_ENV";

    [Fact]
    public async Task IsAuthenticatedAsync_ReturnsTrue_WhenEnvVarIsSet()
    {
        AuthService service = CreateService([MakeConfig("alpha", envVar: TestEnvVar)]);

        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "sk-env-key");
            bool result = await service.IsAuthenticatedAsync("alpha");

            Assert.True(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task IsAuthenticatedAsync_ReturnsTrue_WhenFileCredentialExists()
    {
        FakeFileStore fileStore = new();
        fileStore.Add("beta", new CredentialRecord
        {
            Provider = "beta",
            CredentialType = "apiKey",
            ApiKey = "sk-file-key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        AuthService service = CreateService([MakeConfig("beta")], fileStore);

        bool result = await service.IsAuthenticatedAsync("beta");

        Assert.True(result);
    }

    [Fact]
    public async Task IsAuthenticatedAsync_ReturnsFalse_WhenNoCredentials()
    {
        AuthService service = CreateService([MakeConfig("gamma")]);

        bool result = await service.IsAuthenticatedAsync("gamma");

        Assert.False(result);
    }

    [Fact]
    public async Task IsAuthenticatedAsync_ReturnsFalse_WhenProviderNotConfigured()
    {
        AuthService service = CreateService([MakeConfig("alpha")]);

        bool result = await service.IsAuthenticatedAsync("unknown");

        Assert.False(result);
    }

    [Fact]
    public async Task StoreCredentialAsync_WritesAndCanReadBack()
    {
        FakeFileStore fileStore = new();
        AuthService service = CreateService([MakeConfig("openai")], fileStore);

        await service.StoreCredentialAsync("openai", "sk-stored-key", "bearer");

        CredentialRecord? record = await fileStore.ReadAsync("openai");
        Assert.NotNull(record);
        Assert.Equal("openai", record!.Provider);
        Assert.Equal("sk-stored-key", record.ApiKey);
        Assert.Equal("apiKey", record.CredentialType);
        Assert.Equal("bearer", record.HeaderStyle);
        Assert.NotEqual(default, record.CreatedAt);
        Assert.Equal(record.CreatedAt, record.UpdatedAt);
    }

    [Fact]
    public async Task StoreCredentialAsync_InfersHeaderStyle_ForAnthropic()
    {
        FakeFileStore fileStore = new();
        AuthService service = CreateService([MakeConfig("anthropic", adapter: "anthropic")], fileStore);

        // Pass empty headerStyle — should infer x-api-key for Anthropic.
        await service.StoreCredentialAsync("anthropic", "sk-ant-key", "");

        CredentialRecord? record = await fileStore.ReadAsync("anthropic");
        Assert.NotNull(record);
        Assert.Equal("x-api-key", record!.HeaderStyle);
    }

    [Fact]
    public async Task StoreCredentialAsync_InfersHeaderStyleBearer_ForNonAnthropic()
    {
        FakeFileStore fileStore = new();
        AuthService service = CreateService([MakeConfig("openai")], fileStore);

        // Pass empty headerStyle — should infer bearer for non-Anthropic.
        await service.StoreCredentialAsync("openai", "sk-oai-key", "  ");

        CredentialRecord? record = await fileStore.ReadAsync("openai");
        Assert.NotNull(record);
        Assert.Equal("bearer", record!.HeaderStyle);
    }

    [Fact]
    public async Task LogoutAsync_RemovesCredentialFile()
    {
        FakeFileStore fileStore = new();
        fileStore.Add("gemini", new CredentialRecord
        {
            Provider = "gemini",
            CredentialType = "apiKey",
            ApiKey = "gem-key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        AuthService service = CreateService([MakeConfig("gemini")], fileStore);

        Assert.True(await service.IsAuthenticatedAsync("gemini"));

        await service.LogoutAsync("gemini");

        Assert.False(await service.IsAuthenticatedAsync("gemini"));
    }

    [Fact]
    public async Task LogoutAsync_DoesNotThrow_WhenNoFileExists()
    {
        AuthService service = CreateService([MakeConfig("alpha")]);

        // Should not throw.
        await service.LogoutAsync("alpha");
    }

    [Fact]
    public async Task GetStatus_ReturnsAllProviders_WhenNoFilter()
    {
        AuthService service = CreateService([MakeConfig("a"), MakeConfig("b"), MakeConfig("c")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync();

        Assert.Equal(3, status.Count);
        Assert.Equal("a", status[0].Provider);
        Assert.Equal("b", status[1].Provider);
        Assert.Equal("c", status[2].Provider);
    }

    [Fact]
    public async Task GetStatus_ReturnsSingleProvider_WhenFiltered()
    {
        AuthService service = CreateService([MakeConfig("a"), MakeConfig("b")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("b");

        Assert.Single(status);
        Assert.Equal("b", status[0].Provider);
    }

    [Fact]
    public async Task GetStatus_CaseInsensitive_Filter()
    {
        AuthService service = CreateService([MakeConfig("Anthropic")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("anthropic");

        Assert.Single(status);
        Assert.Equal("Anthropic", status[0].Provider);
    }

    [Fact]
    public async Task GetStatus_ShowsHasEnvVar_WhenSet()
    {
        AuthService service = CreateService([MakeConfig("a", envVar: TestEnvVar)]);

        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "key");
            IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("a");

            Assert.Single(status);
            Assert.True(status[0].HasEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task GetStatus_ShowsHasEnvVarFalse_WhenNotSet()
    {
        AuthService service = CreateService([MakeConfig("a", envVar: "NONEXISTENT_VAR_99999")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("a");

        Assert.Single(status);
        Assert.False(status[0].HasEnvVar);
    }

    [Fact]
    public async Task GetStatus_ShowsHasFileCredential_WhenFileExists()
    {
        FakeFileStore fileStore = new();
        fileStore.Add("a", new CredentialRecord
        {
            Provider = "a",
            CredentialType = "apiKey",
            ApiKey = "key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        AuthService service = CreateService([MakeConfig("a")], fileStore);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("a");

        Assert.True(status[0].HasFileCredential);
    }

    [Fact]
    public async Task GetStatus_ShowsAuthType_FromConfig()
    {
        AuthService service = CreateService([MakeConfig("a", type: "apiKey"), MakeConfig("b", type: "none")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync();

        Assert.Equal("apiKey", status[0].AuthType);
        Assert.Equal("none", status[1].AuthType);
    }

    [Fact]
    public async Task GetStatus_ReturnsEmpty_WhenUnconfiguredProviderFiltered()
    {
        AuthService service = CreateService([MakeConfig("a")]);

        IReadOnlyList<ProviderAuthStatus> status = await service.GetStatusAsync("nonexistent");

        Assert.Empty(status);
    }

    private static AuthService CreateService(
        IEnumerable<ProviderConfig> configs,
        ICredentialFileStore? fileStore = null)
    {
        return new AuthService(configs, fileStore ?? new FakeFileStore());
    }

    private static ProviderConfig MakeConfig(
        string name,
        string? envVar = null,
        string type = "apiKey",
        string adapter = "openai") =>
        new()
        {
            Name = name,
            Adapter = adapter,
            DefaultModelId = "test-model",
            Auth = new ProviderAuthConfig
            {
                Type = type,
                EnvVar = envVar
            },
        };

    private sealed class FakeFileStore : ICredentialFileStore
    {
        private readonly Dictionary<string, CredentialRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string provider, CredentialRecord record) => _records[provider] = record;

        public ValueTask<CredentialRecord?> ReadAsync(string providerName, CancellationToken cancellationToken = default)
        {
            _records.TryGetValue(providerName, out CredentialRecord? record);
            return ValueTask.FromResult(record);
        }

        public ValueTask WriteAsync(CredentialRecord record, CancellationToken cancellationToken = default)
        {
            _records[record.Provider] = record;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string providerName, CancellationToken cancellationToken = default)
        {
            _records.Remove(providerName);
            return ValueTask.CompletedTask;
        }
    }
}
