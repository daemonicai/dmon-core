using Dmon.Abstractions.Providers;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Extensions;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Extensions;

public sealed class StartupExtensionLoaderTests
{
    private static AIFunction MakeFunction(string name) =>
        AIFunctionFactory.Create(() => name, name, $"Test {name}");

    private static ExtensionService MakeService(IEnumerable<IExtensionLoader> loaders) =>
        new(new ToolRegistry(), loaders, NullLogger<ExtensionService>.Instance);

    private static ExtensionService MakeService(IEnumerable<IExtensionLoader> loaders, IProviderRegistry providerRegistry) =>
        new(new ToolRegistry(), loaders, NullLogger<ExtensionService>.Instance, providerRegistry);

    private static StartupExtensionLoader MakeStartupLoader(ExtensionService svc) =>
        new(svc, new EffectiveExtensionSetResolver(), NullLogger<StartupExtensionLoader>.Instance);

    private static IReadOnlyList<ExtensionEntry> MakeEntries(params string[] sources) =>
        sources
            .Select(s => new ExtensionEntry(s, new Dictionary<string, string?>()))
            .ToList();

    /// <summary>
    /// 3.4: one good entry loads its tools; one failing entry is skipped;
    /// the loop completes without throwing (daemon would proceed to AgentReady).
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_GoodAndBad_GoodLoadsAndBadSkipped()
    {
        FakeExtensionLoader goodLoader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "good-ext",
                Description = "Good extension",
                Tools = [MakeFunction("good-tool")],
                SourceKind = "script"
            }
        };

        FakeExtensionLoader badLoader = new("nuget")
        {
            ShouldThrow = true,
            ThrowMessage = "NuGet resolution failed"
        };

        ExtensionService svc = MakeService([goodLoader, badLoader]);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        IReadOnlyList<ExtensionEntry> entries = MakeEntries("good.csx", "nuget:BadPackage");

        // Must not throw — loader guarantees no-abort startup.
        await loader.LoadEntriesAsync(entries);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = svc.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("good-ext", snapshot[0].Name);
        Assert.Equal(1, snapshot[0].ToolCount);
    }

    /// <summary>
    /// 3.4 (error-event path): LoadAsync routes error-sentinel results through the Error event.
    /// The entry with the sentinel is skipped; the good entry still loads.
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_ErrorSentinelEntry_SkippedAndOtherLoads()
    {
        FakeExtensionLoader scriptLoader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "good-ext",
                Description = "Good",
                Tools = [MakeFunction("tool-a")],
                SourceKind = "script"
            }
        };

        FakeExtensionLoader badLoader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "__error__bad",
                Description = "ERROR[load]: Manifest parse failed",
                Tools = [],
                SourceKind = "nuget"
            }
        };

        ExtensionService svc = MakeService([scriptLoader, badLoader]);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        // good first so it lands; bad second to prove loop continues.
        IReadOnlyList<ExtensionEntry> entries = MakeEntries("good.csx", "nuget:BadPackage");

        await loader.LoadEntriesAsync(entries);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = svc.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("good-ext", snapshot[0].Name);
    }

    /// <summary>
    /// 3.3: loading a config-declared entry registers its tools and never invokes ConfirmCallback.
    /// Config presence = prior approval; no prompt should fire.
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_ConfigEntry_RegistersToolsWithoutPrompting()
    {
        bool confirmCalled = false;

        FakeExtensionLoader fakeLoader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "my-ext",
                Description = "My extension",
                Tools = [MakeFunction("my-tool")],
                SourceKind = "script"
            }
        };

        // Installing a ConfirmCallback that would fail the test if invoked.
        // StartupExtensionLoader must never set or invoke this.
        fakeLoader.ConfirmCallback = (_, _) =>
        {
            confirmCalled = true;
            return Task.FromResult(false);
        };

        ExtensionService svc = MakeService([fakeLoader]);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        IReadOnlyList<ExtensionEntry> entries = MakeEntries("my-ext.csx");

        await loader.LoadEntriesAsync(entries);

        Assert.False(confirmCalled, "ConfirmCallback must not be invoked for config-declared extensions.");

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = svc.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("my-ext", snapshot[0].Name);
        Assert.Equal(1, snapshot[0].ToolCount);
    }

    /// <summary>
    /// Empty effective set: LoadEntriesAsync returns immediately without error.
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_EmptySet_CompletesWithNoRegistrations()
    {
        ExtensionService svc = MakeService([]);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        await loader.LoadEntriesAsync(MakeEntries());

        Assert.Empty(svc.GetSnapshot());
    }

    /// <summary>
    /// 3.4 (loop-level catch, provider-throw path): when IProviderRegistry.RegisterExtensionAsync
    /// throws, the loop-level try/catch in LoadEntriesAsync swallows the exception and continues.
    /// A sibling good entry still loads its tools — startup is not aborted.
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_ProviderRegistryThrows_LoopContinuesAndGoodEntryLoads()
    {
        // Loader for the entry whose provider registration will throw.
        FakeExtensionLoader throwingLoader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "provider-ext",
                Description = "Provider extension",
                Tools = [],
                SourceKind = "nuget",
                ProviderExtension = new FakeProviderExtension("bad-provider", isApplicable: true)
            }
        };

        // Loader for the good sibling entry.
        FakeExtensionLoader goodLoader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "good-ext",
                Description = "Good extension",
                Tools = [MakeFunction("good-tool")],
                SourceKind = "script"
            }
        };

        // Registry whose RegisterExtensionAsync throws.
        ThrowingProviderRegistry throwingRegistry = new();

        // Bad entry first to prove the loop doesn't short-circuit.
        ExtensionService svc = MakeService([throwingLoader, goodLoader], throwingRegistry);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        IReadOnlyList<ExtensionEntry> entries = MakeEntries("nuget:bad-provider", "good.csx");

        // Must not throw — the loop-level catch absorbs the provider-registration exception.
        await loader.LoadEntriesAsync(entries);

        // Good entry must still be registered.
        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = svc.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("good-ext", snapshot[0].Name);
        Assert.Equal(1, snapshot[0].ToolCount);
    }

    /// <summary>
    /// 3.4 (ordering): a failing entry that precedes a good entry must not short-circuit
    /// the loop. The good entry loads regardless of entry order.
    /// </summary>
    [Fact]
    public async Task LoadEntriesAsync_FailingBeforeGood_GoodStillLoads()
    {
        FakeExtensionLoader badLoader = new("nuget")
        {
            ShouldThrow = true,
            ThrowMessage = "Assembly not found"
        };

        FakeExtensionLoader goodLoader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "after-fail-ext",
                Description = "Loads after a failure",
                Tools = [MakeFunction("after-tool")],
                SourceKind = "script"
            }
        };

        ExtensionService svc = MakeService([badLoader, goodLoader]);
        StartupExtensionLoader loader = MakeStartupLoader(svc);

        // Failing entry precedes the good entry.
        IReadOnlyList<ExtensionEntry> entries = MakeEntries("nuget:MissingPackage", "good.csx");

        await loader.LoadEntriesAsync(entries);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = svc.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("after-fail-ext", snapshot[0].Name);
    }

    // ---------------------------------------------------------------------------
    // FakeExtensionLoader — mirrors the one in ExtensionServiceTests
    // ---------------------------------------------------------------------------

    private sealed class FakeExtensionLoader : IExtensionLoader
    {
        public string SourceKind { get; }
        public ExtensionLoadResult? Result { get; set; }
        public bool ShouldThrow { get; set; }
        public string ThrowMessage { get; set; } = "Error";
        public Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>>? ConfirmCallback { get; set; }

        public FakeExtensionLoader(string sourceKind)
        {
            SourceKind = sourceKind;
        }

        public Task<ExtensionLoadResult> LoadAsync(
            ParsedExtensionSource source,
            CancellationToken cancellationToken = default)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException(ThrowMessage);
            }

            return Task.FromResult(Result!);
        }
    }

    private sealed class FakeProviderExtension(string providerName, bool isApplicable = true) : IProviderExtension
    {
        public string ProviderName => providerName;
        public bool IsApplicable() => isApplicable;
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public IProviderFactory CreateFactory() => throw new NotSupportedException();
    }

    /// <summary>
    /// IProviderRegistry that throws on RegisterExtensionAsync to exercise the
    /// loop-level catch in StartupExtensionLoader.LoadEntriesAsync.
    /// </summary>
    private sealed class ThrowingProviderRegistry : IProviderRegistry
    {
        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated provider registration failure");

        public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ProviderConfig GetCurrentConfig() => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfig> GetAll() => throw new NotSupportedException();
        public void SetProvider(string name) => throw new NotSupportedException();
        public void SetModel(string modelId) => throw new NotSupportedException();
        public void CycleProvider() => throw new NotSupportedException();
        public void AddDynamicProvider(ProviderConfig config) => throw new NotSupportedException();
        public string? GetCurrentModelId() => null;
        public ProviderSwitchResult? CommitPendingSwitch() => throw new NotSupportedException();
        public bool CurrentSupportsToolCalling => false;
        public bool CurrentSupportsReasoning => false;
    }
}
