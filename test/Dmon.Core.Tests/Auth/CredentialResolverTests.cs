using Dmon.Core.Auth;
using Dmon.Core.Providers;

namespace Dmon.Core.Tests.Auth;

public sealed class CredentialResolverTests
{
    private const string TestEnvVar = "DAEMON_TEST_RESOLVER_ENV";

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenProviderNotConfigured()
    {
        CredentialResolver resolver = CreateResolver([]);

        string? result = await resolver.ResolveAsync("unknown");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNoEnvVarAndNoFile()
    {
        ProviderConfig config = MakeConfig("alpha", envVar: "NOT_SET_VAR_12345", type: "apiKey");
        CredentialResolver resolver = CreateResolver([config]);

        string? result = await resolver.ResolveAsync("alpha");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsEnvVar_WhenSet()
    {
        string expectedKey = "sk-env-test-key";
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, expectedKey);
            ProviderConfig config = MakeConfig("env-provider", envVar: TestEnvVar, type: "apiKey");
            CredentialResolver resolver = CreateResolver([config]);

            string? result = await resolver.ResolveAsync("env-provider");

            Assert.Equal(expectedKey, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsFileCredential_WhenEnvVarNotSet()
    {
        ProviderConfig config = MakeConfig("file-provider", type: "apiKey");
        FakeFileStore fileStore = new();

        fileStore.Add("file-provider", new CredentialRecord
        {
            Provider = "file-provider",
            CredentialType = "apiKey",
            ApiKey = "sk-file-key",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        CredentialResolver resolver = CreateResolver([config], fileStore);

        string? result = await resolver.ResolveAsync("file-provider");

        Assert.Equal("sk-file-key", result);
    }

    [Fact]
    public async Task ResolveAsync_EnvVarTakesPrecedence_OverFileCredential()
    {
        string envKey = "sk-env-wins";
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, envKey);

            ProviderConfig config = MakeConfig("precedence", envVar: TestEnvVar, type: "apiKey");
            FakeFileStore fileStore = new();

            fileStore.Add("precedence", new CredentialRecord
            {
                Provider = "precedence",
                CredentialType = "apiKey",
                ApiKey = "sk-file-loses",
                HeaderStyle = "bearer",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            CredentialResolver resolver = CreateResolver([config], fileStore);

            string? result = await resolver.ResolveAsync("precedence");

            Assert.Equal(envKey, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenAuthTypeIsNone()
    {
        ProviderConfig config = MakeConfig("local", type: "none");
        CredentialResolver resolver = CreateResolver([config]);

        string? result = await resolver.ResolveAsync("local");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive_ProviderNameMatch()
    {
        string expectedKey = "sk-case-key";
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, expectedKey);

            // Config has "Anthropic", resolver queried for "anthropic"
            ProviderConfig config = MakeConfig("Anthropic", envVar: TestEnvVar, type: "apiKey");
            CredentialResolver resolver = CreateResolver([config]);

            string? result = await resolver.ResolveAsync("anthropic");

            Assert.Equal(expectedKey, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenFileHasEmptyApiKey()
    {
        ProviderConfig config = MakeConfig("empty-file", type: "apiKey");
        FakeFileStore fileStore = new();

        fileStore.Add("empty-file", new CredentialRecord
        {
            Provider = "empty-file",
            CredentialType = "apiKey",
            ApiKey = "",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        CredentialResolver resolver = CreateResolver([config], fileStore);

        string? result = await resolver.ResolveAsync("empty-file");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenFileHasWhitespaceApiKey()
    {
        ProviderConfig config = MakeConfig("whitespace-file", type: "apiKey");
        FakeFileStore fileStore = new();

        fileStore.Add("whitespace-file", new CredentialRecord
        {
            Provider = "whitespace-file",
            CredentialType = "apiKey",
            ApiKey = "   ",
            HeaderStyle = "bearer",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        CredentialResolver resolver = CreateResolver([config], fileStore);

        string? result = await resolver.ResolveAsync("whitespace-file");

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenEnvVarIsEmpty()
    {
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, string.Empty);
            ProviderConfig config = MakeConfig("empty-env", envVar: TestEnvVar, type: "apiKey");
            CredentialResolver resolver = CreateResolver([config]);

            string? result = await resolver.ResolveAsync("empty-env");

            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToFile_WhenEnvVarIsWhitespace()
    {
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "   ");
            ProviderConfig config = MakeConfig("ws-env", envVar: TestEnvVar, type: "apiKey");

            FakeFileStore fileStore = new();
            fileStore.Add("ws-env", new CredentialRecord
            {
                Provider = "ws-env",
                CredentialType = "apiKey",
                ApiKey = "sk-fallback-key",
                HeaderStyle = "bearer",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            CredentialResolver resolver = CreateResolver([config], fileStore);

            string? result = await resolver.ResolveAsync("ws-env");

            Assert.Equal("sk-fallback-key", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
        }
    }

    private static CredentialResolver CreateResolver(
        IEnumerable<ProviderConfig> configs,
        ICredentialFileStore? fileStore = null)
    {
        return new CredentialResolver(configs, fileStore ?? new FakeFileStore());
    }

    private static ProviderConfig MakeConfig(string name, string? envVar = null, string type = "apiKey") =>
        new()
        {
            Name = name,
            Adapter = "openai",
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
