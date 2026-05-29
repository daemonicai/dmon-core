using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Tests.Providers;

public sealed class ActiveModelStoreTests
{
    // --- helpers ---

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IConfiguration BuildConfig(string activeModel)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("activeModel", activeModel)])
            .Build();
    }

    // --- Load reads from IConfiguration ---

    [Fact]
    public void Load_ReadsActiveModelFromConfiguration()
    {
        IConfiguration config = BuildConfig("gemini/gemini-2.0-flash-lite");
        ActiveModelStore store = new(config, Path.GetTempPath());

        ModelRef? result = store.Load();

        Assert.NotNull(result);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("gemini-2.0-flash-lite", result.Model);
    }

    [Fact]
    public void Load_AbsentKey_ReturnsNull()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        ActiveModelStore store = new(config, Path.GetTempPath());

        ModelRef? result = store.Load();

        Assert.Null(result);
    }

    [Fact]
    public void Load_ProviderOnly_ReturnsModelRefWithNullModel()
    {
        IConfiguration config = BuildConfig("anthropic");
        ActiveModelStore store = new(config, Path.GetTempPath());

        ModelRef? result = store.Load();

        Assert.NotNull(result);
        Assert.Equal("anthropic", result.Provider);
        Assert.Null(result.Model);
    }

    // --- SaveAsync writes config.local.yaml ---

    [Fact]
    public async Task SaveAsync_WritesActiveModelToConfigLocalYaml()
    {
        string tempDir = CreateTempDir();
        try
        {
            IConfiguration config = BuildConfig("gemini/x");
            ActiveModelStore store = new(config, tempDir);

            await store.SaveAsync(new ModelRef("gemini", "gemini-2.0-flash-lite"));

            string filePath = Path.Combine(tempDir, ".dmon", "config.local.yaml");
            Assert.True(File.Exists(filePath));
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("activeModel: gemini/gemini-2.0-flash-lite", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesConfigLocalYaml_ThenReadableViaNewConfiguration()
    {
        string tempDir = CreateTempDir();
        try
        {
            // Use a real IConfiguration backed by the file we are about to write.
            string localYaml = Path.Combine(tempDir, ".dmon", "config.local.yaml");
            IConfiguration config = new ConfigurationBuilder()
                .AddYamlFile(localYaml, optional: true, reloadOnChange: false)
                .Build();

            ActiveModelStore store = new(config, tempDir);
            await store.SaveAsync(new ModelRef("openai", "gpt-4o"));

            // Build a fresh IConfiguration from the written file.
            IConfiguration reloaded = new ConfigurationBuilder()
                .AddYamlFile(localYaml, optional: false, reloadOnChange: false)
                .Build();

            ActiveModelStore reloadedStore = new(reloaded, tempDir);
            ModelRef? result = reloadedStore.Load();

            Assert.NotNull(result);
            Assert.Equal("openai", result.Provider);
            Assert.Equal("gpt-4o", result.Model);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_PreservesOtherTopLevelKeys()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            string filePath = Path.Combine(dmonDir, "config.local.yaml");

            // Pre-populate with another key.
            await File.WriteAllTextAsync(filePath, "someOtherKey: someValue\n");

            IConfiguration config = new ConfigurationBuilder().Build();
            ActiveModelStore store = new(config, tempDir);
            await store.SaveAsync(new ModelRef("anthropic", "claude-opus-4-5"));

            string content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("someOtherKey: someValue", content, StringComparison.Ordinal);
            Assert.Contains("activeModel: anthropic/claude-opus-4-5", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingActiveModelLine()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            string filePath = Path.Combine(dmonDir, "config.local.yaml");

            await File.WriteAllTextAsync(filePath, "activeModel: oldProvider/oldModel\n");

            IConfiguration config = new ConfigurationBuilder().Build();
            ActiveModelStore store = new(config, tempDir);
            await store.SaveAsync(new ModelRef("gemini", "gemini-2.0-flash-lite"));

            string content = await File.ReadAllTextAsync(filePath);
            // New value present, old value absent.
            Assert.Contains("activeModel: gemini/gemini-2.0-flash-lite", content, StringComparison.Ordinal);
            Assert.DoesNotContain("oldProvider", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ModelRefWithNullModel_WritesProviderOnlyString()
    {
        string tempDir = CreateTempDir();
        try
        {
            IConfiguration config = new ConfigurationBuilder().Build();
            ActiveModelStore store = new(config, tempDir);

            await store.SaveAsync(new ModelRef("anthropic", null));

            string filePath = Path.Combine(tempDir, ".dmon", "config.local.yaml");
            string content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("activeModel: anthropic", content, StringComparison.Ordinal);
            Assert.DoesNotContain("anthropic/", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesDmonDirectory_IfAbsent()
    {
        string tempDir = CreateTempDir();
        try
        {
            // No .dmon directory pre-created.
            IConfiguration config = new ConfigurationBuilder().Build();
            ActiveModelStore store = new(config, tempDir);

            // Must not throw even when .dmon is absent.
            await store.SaveAsync(new ModelRef("gemini", "gemini-2.0-flash-lite"));

            Assert.True(Directory.Exists(Path.Combine(tempDir, ".dmon")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".dmon", "config.local.yaml")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
