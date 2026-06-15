using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Core.Tests.Hosting;

/// <summary>
/// Tests verifying configuration precedence and the <see cref="DmonHostBuilder.ConfigureConfiguration"/>
/// hook introduced by task 6.2 (composition-root-hosting change).
///
/// These tests mutate <see cref="Directory.GetCurrentDirectory"/>, which is process-wide state.
/// They are serialised via <c>[Collection("FileSystemCwd")]</c>.
/// </summary>
[Collection("FileSystemCwd")]
public sealed class ConfigurationPrecedenceTests : IDisposable
{
    // Restore to a path that is guaranteed to exist after the temp dir is deleted.
    private static readonly string StableRestoreDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly string _tempDir;

    public ConfigurationPrecedenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(StableRestoreDir);
        Directory.Delete(_tempDir, recursive: true);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void WriteDmonConfig(string yaml)
    {
        string dmonDir = Path.Combine(_tempDir, ".dmon");
        Directory.CreateDirectory(dmonDir);
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), yaml);
    }

    private static DmonHostBuilder CreateBuilder() =>
        DmonHost.CreateBuilder([])
            .WithStdio(new StringReader(string.Empty), new StreamWriter(Stream.Null))
            .WithoutTelemetry();

    // ── 6.3.1 — WithModel wins over config.yaml activeModel ─────────────────

    /// <summary>
    /// When <c>config.yaml</c> declares <c>activeModel: providerA/modelA</c> and the
    /// composition root calls <see cref="DmonHostBuilder.WithModel"/>, the builder-supplied
    /// value wins and <see cref="IActiveModelStore.Load"/> returns the builder value.
    /// </summary>
    [Fact]
    public void Build_WithModel_WinsOverConfigYamlActiveModel()
    {
        WriteDmonConfig("activeModel: providerA/modelA\n");

        DmonBuiltHost host = CreateBuilder()
            .WithModel("providerB", "modelB")
            .Build();

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("providerB", loaded.Provider);
        Assert.Equal("modelB", loaded.Model);
    }

    // ── 6.3.3a — ConfigureConfiguration can read merged config ───────────────

    /// <summary>
    /// The <see cref="DmonHostBuilder.ConfigureConfiguration"/> callback receives the
    /// fully-merged <see cref="IConfigurationManager"/> and can read values that were
    /// set in <c>config.yaml</c>.
    /// </summary>
    [Fact]
    public void ConfigureConfiguration_CanReadMergedConfigValue()
    {
        WriteDmonConfig("sessionStore: global\n");

        string? capturedSessionStore = null;

        DmonBuiltHost host = CreateBuilder()
            .ConfigureConfiguration(cfg =>
            {
                capturedSessionStore = cfg["sessionStore"];
            })
            .Build();

        // Dispose the host (don't run it — we only care about the Build()-time callback).
        _ = host;

        Assert.Equal("global", capturedSessionStore);
    }

    // ── 6.3.3b — ConfigureConfiguration override wins over WithModel ─────────

    /// <summary>
    /// A <see cref="DmonHostBuilder.ConfigureConfiguration"/> callback that adds an
    /// in-memory source overrides <see cref="DmonHostBuilder.WithModel"/> because
    /// ConfigureConfiguration runs after WithModel in <c>Build()</c>.
    /// </summary>
    [Fact]
    public void ConfigureConfiguration_Override_WinsOverWithModel()
    {
        DmonBuiltHost host = CreateBuilder()
            .WithModel("providerA", "modelA")
            .ConfigureConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["activeModel"] = "providerC/modelC",
                });
            })
            .Build();

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("providerC", loaded.Provider);
        Assert.Equal("modelC", loaded.Model);
    }

    // ── 6.3.3c — Multiple ConfigureConfiguration calls compose in order ──────

    /// <summary>
    /// When <see cref="DmonHostBuilder.ConfigureConfiguration"/> is called multiple times,
    /// callbacks run in registration order, so the last registered in-memory source wins.
    /// </summary>
    [Fact]
    public void ConfigureConfiguration_MultipleCalls_LastWins()
    {
        DmonBuiltHost host = CreateBuilder()
            .ConfigureConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["activeModel"] = "providerX/modelX",
                });
            })
            .ConfigureConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["activeModel"] = "providerY/modelY",
                });
            })
            .Build();

        IActiveModelStore store = host.Services.GetRequiredService<IActiveModelStore>();
        ModelRef? loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("providerY", loaded.Provider);
        Assert.Equal("modelY", loaded.Model);
    }
}
