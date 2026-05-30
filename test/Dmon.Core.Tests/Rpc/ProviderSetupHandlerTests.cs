using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;

namespace Dmon.Core.Tests.Rpc;

public sealed class ProviderSetupHandlerTests : IDisposable
{
    private readonly string _tempDir;

    public ProviderSetupHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dmon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── helpers ──────────────────────────────────────────────

    private ProviderConfigureCommand MakeCommand(
        string scope,
        string adapter = "anthropic",
        string modelId = "claude-sonnet-4-6",
        string envVar = "ANTHROPIC_API_KEY") =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Scope = scope,
            Adapter = adapter,
            ModelId = modelId,
            EnvVar = envVar
        };

    private static IReadOnlyList<ProviderConfig> LoadFromFile(string path)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddYamlFile(path, optional: false, reloadOnChange: false)
            .Build();
        return new ProviderConfigLoader(config).Load();
    }

    // Redirects global/local path resolution to temp paths so tests never
    // touch ~/.dmon/ or CWD/.dmon/.
    private sealed class TestablePsh(
        IEventEmitter emitter,
        string globalConfigPath,
        string localConfigPath,
        IProviderRegistry? registry = null) : ProviderSetupHandler(emitter, registry ?? new NoOpProviderRegistry(), [])
    {
        protected override string? ResolveConfigPath(string scope) =>
            scope switch
            {
                "global" => globalConfigPath,
                "local" => localConfigPath,
                _ => null
            };
    }

    private sealed class NoOpProviderRegistry : IProviderRegistry
    {
        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfig> GetAll() => throw new NotSupportedException();
        public void SetProvider(string name) { }
        public void SetModel(string modelId) { }
        public void CycleProvider() { }
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void AddDynamicProvider(ProviderConfig config) { }
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => null;
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }

    // ─── tests ────────────────────────────────────────────────

    [Fact]
    public async Task NewGlobalFile_WritesCorrectYaml()
    {
        string globalPath = Path.Combine(_tempDir, "global", ".dmon", "config.yaml");
        string localPath = Path.Combine(_tempDir, "local", ".dmon", "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, globalPath, localPath);

        await handler.ConfigureAsync(MakeCommand("global"), CancellationToken.None);

        Assert.True(File.Exists(globalPath), "Global config file was not created.");
        IReadOnlyList<ProviderConfig> providers = LoadFromFile(globalPath);
        Assert.Single(providers);
        ProviderConfig p = providers[0];
        Assert.Equal("anthropic", p.Adapter);
        Assert.Equal("claude-sonnet-4-6", p.DefaultModelId);
        Assert.Equal("envVar", p.Auth.Type);
        Assert.Equal("ANTHROPIC_API_KEY", p.Auth.EnvVar);
    }

    [Fact]
    public async Task NewLocalFile_WritesCorrectYaml()
    {
        string globalPath = Path.Combine(_tempDir, "global", ".dmon", "config.yaml");
        string localPath = Path.Combine(_tempDir, "local", ".dmon", "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, globalPath, localPath);

        await handler.ConfigureAsync(
            MakeCommand("local", adapter: "openai", modelId: "gpt-4o", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        Assert.True(File.Exists(localPath), "Local config file was not created.");
        IReadOnlyList<ProviderConfig> providers = LoadFromFile(localPath);
        Assert.Single(providers);
        ProviderConfig p = providers[0];
        Assert.Equal("openai", p.Adapter);
        Assert.Equal("gpt-4o", p.DefaultModelId);
        Assert.Equal("envVar", p.Auth.Type);
        Assert.Equal("OPENAI_API_KEY", p.Auth.EnvVar);
    }

    [Fact]
    public async Task ExistingFile_BootstrapStub_AppendsCorrectly()
    {
        // The bootstrap stub has a `providers:` key with only commented children —
        // the handler must insert the stanza directly under `providers:`.
        const string bootstrapStub =
            "# dmon configuration\n" +
            "# See docs/configuration.md for full reference.\n" +
            "\n" +
            "sessionStore: local\n" +
            "\n" +
            "providers:\n" +
            "  # example:\n" +
            "  #   adapter: anthropic\n" +
            "  #   defaultModelId: claude-sonnet-4-20250514\n" +
            "  #   auth:\n" +
            "  #     type: envVar\n" +
            "  #     envVar: ANTHROPIC_API_KEY\n";

        string configPath = Path.Combine(_tempDir, "config.yaml");
        await File.WriteAllTextAsync(configPath, bootstrapStub);

        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(MakeCommand("global"), CancellationToken.None);

        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        Assert.Single(providers);
        ProviderConfig p = providers[0];
        Assert.Equal("anthropic", p.Adapter);
        Assert.Equal("envVar", p.Auth.Type);
        Assert.Equal("ANTHROPIC_API_KEY", p.Auth.EnvVar);
    }

    [Fact]
    public async Task ExistingFile_WithExistingProvider_AppendsSecondProvider()
    {
        const string existingContent =
            "providers:\n" +
            "  openai:\n" +
            "    adapter: openai\n" +
            "    defaultModelId: gpt-4o\n" +
            "    auth:\n" +
            "      type: envVar\n" +
            "      envVar: OPENAI_API_KEY\n";

        string configPath = Path.Combine(_tempDir, "config.yaml");
        await File.WriteAllTextAsync(configPath, existingContent);

        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(MakeCommand("global"), CancellationToken.None);

        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Adapter == "openai");
        Assert.Contains(providers, p => p.Adapter == "anthropic");
    }

    [Fact]
    public async Task EmitsProviderConfiguredEvent_OnSuccess()
    {
        string configPath = Path.Combine(_tempDir, "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(MakeCommand("global"), CancellationToken.None);

        ProviderConfiguredEvent evt = Assert.Single(emitter.Emitted.OfType<ProviderConfiguredEvent>());
        Assert.Equal("anthropic", evt.Adapter);
        Assert.Equal("claude-sonnet-4-6", evt.ModelId);
        Assert.Equal("global", evt.Scope);
    }

    [Fact]
    public async Task EmitsErrorEvent_OnInvalidScope()
    {
        // Use base handler directly — TestablePsh passes unknown scopes through to null.
        FakeEventEmitter emitter = new();
        ProviderSetupHandler handler = new(emitter, new NoOpProviderRegistry(), []);

        await handler.ConfigureAsync(MakeCommand("unknown-scope"), CancellationToken.None);

        ErrorEvent evt = Assert.Single(emitter.Emitted.OfType<ErrorEvent>());
        Assert.Equal("provider.configure.failed", evt.Code);
        Assert.True(evt.Recoverable);
    }

    [Fact]
    public async Task EmitsErrorEvent_OnEmptyAdapter()
    {
        string configPath = Path.Combine(_tempDir, "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(MakeCommand("global", adapter: ""), CancellationToken.None);

        ErrorEvent evt = Assert.Single(emitter.Emitted.OfType<ErrorEvent>());
        Assert.Equal("provider.configure.failed", evt.Code);
        Assert.True(evt.Recoverable);
    }

    // ─── Bug A: upsert — duplicate key prevention ─────────────────────

    [Fact]
    public async Task SameProvider_PersistTwice_ProducesExactlyOneBlock()
    {
        // Persisting "openai" twice must result in exactly ONE `openai:` block (upsert, not append).
        string configPath = Path.Combine(_tempDir, "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o-mini", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        string written = await File.ReadAllTextAsync(configPath);

        // Exactly one `openai:` key in the file.
        int count = 0;
        int pos = 0;
        while ((pos = written.IndexOf("  openai:", pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos++;
        }
        Assert.Equal(1, count);

        // The retained block must use the model from the second persist.
        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        ProviderConfig openai = Assert.Single(providers);
        Assert.Equal("openai", openai.Adapter);
        Assert.Equal("gpt-4o-mini", openai.DefaultModelId);
    }

    [Fact]
    public async Task SameProvider_PersistTwice_FileParsesByYamlWithoutError()
    {
        // Round-trip: write twice then parse — must not throw a duplicate-key exception.
        string configPath = Path.Combine(_tempDir, "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o-mini", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        // Parsing must succeed without throwing — duplicate keys would cause an exception here.
        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        Assert.Single(providers);
    }

    [Fact]
    public async Task DifferentProviders_PersistBoth_BothPresent()
    {
        // Persisting "openai" then "anthropic" keeps both blocks — upsert must not
        // accidentally remove sibling providers.
        string configPath = Path.Combine(_tempDir, "config.yaml");
        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "anthropic", modelId: "claude-sonnet-4-6", envVar: "ANTHROPIC_API_KEY"),
            CancellationToken.None);

        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        Assert.Equal(2, providers.Count);
        Assert.Contains(providers, p => p.Adapter == "openai");
        Assert.Contains(providers, p => p.Adapter == "anthropic");
    }

    [Fact]
    public async Task SameProvider_Upsert_PreservesOtherTopLevelKeys()
    {
        // A file with a non-providers top-level key must survive a same-provider upsert.
        const string initial =
            "sessionStore: local\n" +
            "\n" +
            "providers:\n" +
            "  openai:\n" +
            "    adapter: openai\n" +
            "    defaultModelId: gpt-4o\n" +
            "    auth:\n" +
            "      type: envVar\n" +
            "      envVar: OPENAI_API_KEY\n";

        string configPath = Path.Combine(_tempDir, "config.yaml");
        await File.WriteAllTextAsync(configPath, initial);

        FakeEventEmitter emitter = new();
        TestablePsh handler = new(emitter, configPath, configPath);

        await handler.ConfigureAsync(
            MakeCommand("global", adapter: "openai", modelId: "gpt-4o-mini", envVar: "OPENAI_API_KEY"),
            CancellationToken.None);

        string written = await File.ReadAllTextAsync(configPath);

        // sessionStore key must be preserved.
        Assert.Contains("sessionStore:", written, StringComparison.Ordinal);

        // Exactly one openai block.
        IReadOnlyList<ProviderConfig> providers = LoadFromFile(configPath);
        ProviderConfig openai = Assert.Single(providers);
        Assert.Equal("gpt-4o-mini", openai.DefaultModelId);
    }
}
