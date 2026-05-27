using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Core.Providers;
using Dmon.Extensions;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Extensions;

public sealed class ExtensionServiceTests
{
    private static AIFunction MakeFunction(string name) =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(
            () => name,
            name,
            $"Test {name}");

    private static IDmonExtension MakeExtension(string name) => new StubExtension(name);

    private static ExtensionService MakeService(
        IToolRegistry registry,
        IEnumerable<IExtensionLoader> loaders,
        IProviderRegistry? providerRegistry = null) =>
        new(registry, loaders, NullLogger<ExtensionService>.Instance, providerRegistry);

    private sealed class StubExtension(string name) : IDmonExtension
    {
        public string Name => name;
        public string Description => $"Stub {name}";
        public IEnumerable<AIFunction> Tools => [];
    }

    [Fact]
    public async Task LoadAsync_ScriptExtension_RegistersAndEmitsEvent()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionLoadedEvent? loadedEvent = null;

        // Create a fake script loader that always succeeds.
        FakeExtensionLoader loader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "test",
                Description = "test desc",
                Tools = [MakeFunction("f1"), MakeFunction("f2")],
                SourceKind = "script"
            }
        };

        ExtensionService service = MakeService(registry, [loader]);
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("test.csx");

        Assert.NotNull(loadedEvent);
        Assert.Equal("test", loadedEvent!.Name);
        Assert.Equal(2, loadedEvent.Tools.Count);
        Assert.Contains("f1", loadedEvent.Tools);
        Assert.Contains("f2", loadedEvent.Tools);

        // Verify registry.
        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal("test", snapshot[0].Name);
        Assert.Equal(2, snapshot[0].ToolCount);
    }

    [Fact]
    public async Task LoadAsync_ExtensionError_EmitsErrorEvent()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionErrorEvent? errorEvent = null;

        // Create a loader that returns an error.
        FakeExtensionLoader loader = new("script")
        {
            Result = new ExtensionLoadResult
            {
                Name = "__error__test",
                Description = "ERROR[load]: Something failed",
                Tools = [],
                SourceKind = "script"
            }
        };

        ExtensionService service = MakeService(registry, [loader]);
        service.Error += e => errorEvent = e;

        await service.LoadAsync("test.csx");

        Assert.NotNull(errorEvent);
        Assert.Equal("load", errorEvent!.Phase);

        // Registry should be empty — no partial registration.
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public async Task LoadAsync_UnknownSourceKind_EmitsErrorEvent()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionErrorEvent? errorEvent = null;

        ExtensionService service = MakeService(registry, []);
        service.Error += e => errorEvent = e;

        await service.LoadAsync("invalid:source");

        Assert.NotNull(errorEvent);
        Assert.Equal("parse", errorEvent!.Phase);
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public async Task LoadAsync_LoaderThrows_EmitsErrorEvent()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionErrorEvent? errorEvent = null;

        FakeExtensionLoader loader = new("script")
        {
            ShouldThrow = true,
            ThrowMessage = "Boom"
        };

        ExtensionService service = MakeService(registry, [loader]);
        service.Error += e => errorEvent = e;

        await service.LoadAsync("test.csx");

        Assert.NotNull(errorEvent);
        Assert.Equal("execute", errorEvent!.Phase);
        Assert.Contains("Boom", errorEvent.Diagnostics[0].ToString());
    }

    [Fact]
    public void Unload_RemovesAndEmitsEvent()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionUnloadedEvent? unloadedEvent = null;

        registry.Register("myext", MakeExtension("myext"), [MakeFunction("f")]);

        ExtensionService service = MakeService(registry, []);
        service.Unloaded += e => unloadedEvent = e;

        service.Unload("myext");

        Assert.NotNull(unloadedEvent);
        Assert.Equal("myext", unloadedEvent!.Name);
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Unload_DoesNotThrow_WhenNotRegistered()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionService service = MakeService(registry, []);

        // Should not throw.
        service.Unload("nonexistent");
    }

    [Fact]
    public void Clear_RemovesAllExtensions()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", MakeExtension("a"), [MakeFunction("f")]);
        registry.Register("b", MakeExtension("b"), [MakeFunction("f")]);

        ExtensionService service = MakeService(registry, []);
        service.Clear();

        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", MakeExtension("a"), [MakeFunction("f1"), MakeFunction("f2")]);
        registry.Register("b", MakeExtension("b"), [MakeFunction("f3")]);

        ExtensionService service = MakeService(registry, []);

        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = service.GetSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, s => s.Name == "a" && s.ToolCount == 2);
        Assert.Contains(snapshot, s => s.Name == "b" && s.ToolCount == 1);
    }

    /// <summary>
    /// Verifies that local assembly sources (.dll paths) are routed to the
    /// "nuget" loader, since NuGetExtensionLoader handles both NuGet packages
    /// and local .dll files.
    /// </summary>
    [Fact]
    public async Task LoadAsync_AssemblySource_RoutesToNugetLoader()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionLoadedEvent? loadedEvent = null;
        bool loaderCalled = false;

        // Simulate a NuGetExtensionLoader via a fake with SourceKind="nuget".
        FakeExtensionLoader nugetLoader = new("nuget")
        {
            OnLoad = () => loaderCalled = true,
            Result = new ExtensionLoadResult
            {
                Name = "MyAssemblyExtension",
                Description = "Loaded from local assembly",
                Tools = [MakeFunction("asm_tool")],
                SourceKind = "nuget"
            }
        };

        ExtensionService service = MakeService(registry, [nugetLoader]);
        service.Loaded += e => loadedEvent = e;

        // "assembly" source should route to the nuget loader.
        await service.LoadAsync("./myext.dll");

        Assert.True(loaderCalled, "NuGet loader was not invoked for assembly source.");
        Assert.NotNull(loadedEvent);
        Assert.Equal("MyAssemblyExtension", loadedEvent!.Name);
        Assert.Single(loadedEvent.Tools);
        Assert.Equal("asm_tool", loadedEvent.Tools[0]);
    }

    /// <summary>
    /// Verifies that a NuGet-source prefix like "nuget:MyPackage" still routes
    /// to the nuget loader correctly (regression check).
    /// </summary>
    [Fact]
    public async Task LoadAsync_NugetPrefix_RoutesToNugetLoader()
    {
        IToolRegistry registry = new ToolRegistry();
        ExtensionLoadedEvent? loadedEvent = null;
        bool loaderCalled = false;

        FakeExtensionLoader nugetLoader = new("nuget")
        {
            OnLoad = () => loaderCalled = true,
            Result = new ExtensionLoadResult
            {
                Name = "NuGetExtension",
                Description = "Loaded from NuGet",
                Tools = [MakeFunction("nuget_tool")],
                SourceKind = "nuget"
            }
        };

        ExtensionService service = MakeService(registry, [nugetLoader]);
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("nuget:MyPackage");

        Assert.True(loaderCalled);
        Assert.NotNull(loadedEvent);
        Assert.Equal("NuGetExtension", loadedEvent!.Name);
    }

    // ------------------------------------------------------------------ //
    // Provider extension routing tests                                     //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task LoadAsync_ProviderExtension_IsApplicableFalse_LogsWarning_NotRegistered()
    {
        IToolRegistry registry = new ToolRegistry();
        FakeProviderRegistry providerRegistry = new();
        FakeProviderExtension providerExt = new("test-provider", isApplicable: false);
        ExtensionErrorEvent? errorEvent = null;
        ExtensionLoadedEvent? loadedEvent = null;

        FakeExtensionLoader loader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "test-provider",
                Tools = [],
                SourceKind = "nuget",
                ProviderExtension = providerExt
            }
        };

        ExtensionService service = MakeService(registry, [loader], providerRegistry);
        service.Error += e => errorEvent = e;
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("nuget:test-provider");

        // Not-applicable provider with no tools → error (nothing registered)
        Assert.NotNull(errorEvent);
        Assert.Null(loadedEvent);
        Assert.Empty(providerRegistry.Registered);
    }

    [Fact]
    public async Task LoadAsync_ProviderExtension_IsApplicableTrue_RegistersInRegistry()
    {
        IToolRegistry registry = new ToolRegistry();
        FakeProviderRegistry providerRegistry = new();
        FakeProviderExtension providerExt = new("test-provider", isApplicable: true);
        ExtensionLoadedEvent? loadedEvent = null;

        FakeExtensionLoader loader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "test-provider",
                Tools = [],
                SourceKind = "nuget",
                ProviderExtension = providerExt
            }
        };

        ExtensionService service = MakeService(registry, [loader], providerRegistry);
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("nuget:test-provider");

        Assert.NotNull(loadedEvent);
        Assert.Equal("test-provider", loadedEvent!.ProviderName);
        Assert.Single(providerRegistry.Registered);
        Assert.Same(providerExt, providerRegistry.Registered[0]);
    }

    [Fact]
    public async Task LoadAsync_ExtensionWithBothToolsAndProvider_RegistersBoth()
    {
        IToolRegistry registry = new ToolRegistry();
        FakeProviderRegistry providerRegistry = new();
        FakeProviderExtension providerExt = new("combo-provider", isApplicable: true);
        ExtensionLoadedEvent? loadedEvent = null;

        FakeExtensionLoader loader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "combo-ext",
                Tools = [MakeFunction("combo_tool")],
                SourceKind = "nuget",
                ProviderExtension = providerExt
            }
        };

        ExtensionService service = MakeService(registry, [loader], providerRegistry);
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("nuget:combo-ext");

        Assert.NotNull(loadedEvent);
        Assert.Equal("combo-provider", loadedEvent!.ProviderName);
        Assert.Single(loadedEvent.Tools);
        Assert.Equal("combo_tool", loadedEvent.Tools[0]);
        Assert.Single(providerRegistry.Registered);
        Assert.Single(registry.GetSnapshot());
    }

    [Fact]
    public async Task LoadAsync_ExtensionWithNeitherToolsNorProvider_EmitsError()
    {
        IToolRegistry registry = new ToolRegistry();
        FakeProviderRegistry providerRegistry = new();
        ExtensionErrorEvent? errorEvent = null;

        FakeExtensionLoader loader = new("nuget")
        {
            Result = new ExtensionLoadResult
            {
                Name = "empty-ext",
                Tools = [],
                SourceKind = "nuget"
            }
        };

        ExtensionService service = MakeService(registry, [loader], providerRegistry);
        service.Error += e => errorEvent = e;

        await service.LoadAsync("nuget:empty-ext");

        Assert.NotNull(errorEvent);
        Assert.Equal("load", errorEvent!.Phase);
        Assert.Empty(providerRegistry.Registered);
        Assert.Empty(registry.GetSnapshot());
    }

    private sealed class FakeExtensionLoader : IExtensionLoader
    {
        public string SourceKind { get; }
        public ExtensionLoadResult? Result { get; set; }
        public bool ShouldThrow { get; set; }
        public string ThrowMessage { get; set; } = "Error";
        public Action? OnLoad { get; set; }
        public Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>>? ConfirmCallback { get; set; }

        public FakeExtensionLoader(string sourceKind)
        {
            SourceKind = sourceKind;
        }

        public Task<ExtensionLoadResult> LoadAsync(
            ParsedExtensionSource source,
            CancellationToken cancellationToken = default)
        {
            OnLoad?.Invoke();

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

    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        public List<IProviderExtension> Registered { get; } = [];

        public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
        {
            Registered.Add(extension);
            return Task.CompletedTask;
        }

        // Unused members for this set of tests.
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
