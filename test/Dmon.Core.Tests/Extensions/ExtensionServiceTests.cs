using Dmon.Core.Extensions;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Extensions;

public sealed class ExtensionServiceTests
{
    private static AIFunction MakeFunction(string name) =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(
            () => name,
            name,
            $"Test {name}");

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

        ExtensionService service = new(registry, [loader]);
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

        ExtensionService service = new(registry, [loader]);
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

        ExtensionService service = new(registry, []);
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

        ExtensionService service = new(registry, [loader]);
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

        registry.Register("myext", [MakeFunction("f")]);

        ExtensionService service = new(registry, []);
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
        ExtensionService service = new(registry, []);

        // Should not throw.
        service.Unload("nonexistent");
    }

    [Fact]
    public void Clear_RemovesAllExtensions()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", [MakeFunction("f")]);
        registry.Register("b", [MakeFunction("f")]);

        ExtensionService service = new(registry, []);
        service.Clear();

        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        IToolRegistry registry = new ToolRegistry();
        registry.Register("a", [MakeFunction("f1"), MakeFunction("f2")]);
        registry.Register("b", [MakeFunction("f3")]);

        ExtensionService service = new(registry, []);

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

        ExtensionService service = new(registry, [nugetLoader]);
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

        ExtensionService service = new(registry, [nugetLoader]);
        service.Loaded += e => loadedEvent = e;

        await service.LoadAsync("nuget:MyPackage");

        Assert.True(loaderCalled);
        Assert.NotNull(loadedEvent);
        Assert.Equal("NuGetExtension", loadedEvent!.Name);
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
}
