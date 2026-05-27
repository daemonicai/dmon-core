using System.Reflection;
using System.Runtime.Loader;
using Dmon.Core.Extensions;
using Dmon.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Extensions;

public sealed class NuGetExtensionLoaderTests : IDisposable
{
    // Tests load self-contained assemblies that use parameterless constructors;
    // a null service provider is sufficient here.
    private static readonly IServiceProvider NullSp = new NullServiceProvider();
    private readonly string _tempDir;

    public NuGetExtensionLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-ext-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LoadAsync_LocalAssembly_DiscoversAndInstantiatesExtensions()
    {
        // Use the test assembly itself which contains TestExtension and SecondTestExtension.
        string assemblyPath = typeof(TestExtension).Assembly.Location;
        NuGetExtensionLoader loader = new(NullSp);

        ParsedExtensionSource source = new()
        {
            Kind = "assembly",
            Value = assemblyPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.NotEmpty(result.Tools);
        Assert.Equal("TestExtension", result.Name);
        Assert.Contains(result.Tools, f => f.Name == "TestHello");
        Assert.Contains(result.Tools, f => f.Name == "TestAdd");
        // SecondTestExtension tools should also be included.
        Assert.Contains(result.Tools, f => f.Name == "TestMeaning");
        Assert.Equal("nuget", result.SourceKind);
    }

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new()
        {
            Kind = "assembly",
            Value = "/nonexistent/path/to/extension.dll"
        };

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(source));
    }

    [Fact]
    public async Task LoadAsync_ConfirmCallback_CalledForBothPhases()
    {
        string assemblyPath = typeof(TestExtension).Assembly.Location;
        NuGetExtensionLoader loader = new(NullSp);

        List<ExtensionLoadConfirmRequest> calls = [];
        loader.ConfirmCallback = (request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(true);
        };

        ParsedExtensionSource source = new()
        {
            Kind = "assembly",
            Value = assemblyPath
        };

        await loader.LoadAsync(source);

        // Should have been called for "load" phase.
        Assert.Contains(calls, c => c.Phase == "load");
    }

    [Fact]
    public async Task LoadAsync_ConfirmCallbackDenied_ReturnsErrorResult()
    {
        string assemblyPath = typeof(TestExtension).Assembly.Location;
        NuGetExtensionLoader loader = new(NullSp);
        loader.ConfirmCallback = (_, _) => Task.FromResult(false);

        ParsedExtensionSource source = new()
        {
            Kind = "assembly",
            Value = assemblyPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        // Error results have a __error__ prefix and empty tools.
        Assert.StartsWith("__error__", result.Name);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task LoadAsync_NuGetSource_CallsConfirmWithPackageInfo()
    {
        NuGetExtensionLoader loader = new(NullSp);
        List<ExtensionLoadConfirmRequest> calls = [];
        loader.ConfirmCallback = (request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(false); // Deny to avoid actual NuGet resolution.
        };

        ParsedExtensionSource source = new()
        {
            Kind = "nuget",
            Value = "SomePackage",
            Version = "1.2.3"
        };

        await loader.LoadAsync(source);

        Assert.Contains(calls, c =>
            c.Phase == "resolve"
            && c.PackageId == "SomePackage"
            && c.PackageVersion == "1.2.3");
    }

    /// <summary>
    /// Spec: "Local assembly loads into the Default context" and
    /// "no collectible AssemblyLoadContext is created".
    /// </summary>
    [Fact]
    public async Task LoadAsync_LocalAssembly_LoadsIntoDefaultALC_AndCreatesNoCollectibleContext()
    {
        // Emit a fresh assembly with a unique name so Default's first-writer-wins
        // cache does not interfere with the main test assembly already resident.
        string guid = Guid.NewGuid().ToString("N");
        string extName = $"ExtAlc{guid}";
        string extDir = Path.Combine(_tempDir, "alc");
        Directory.CreateDirectory(extDir);
        string dllPath = Path.Combine(extDir, extName + ".dll");

        TestAssemblyEmitter.EmitAssembly(
            assemblyName: extName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{extName}}Extension : IDmonExtension
                {
                    public string Name => "{{extName}}";
                    public string Description => "test";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "hi", "{{extName}}_Hi", "greet")];
                }
                """,
            outputPath: dllPath,
            additionalRefs: []);

        int collectibleBefore = AssemblyLoadContext.All.Count(c => c.IsCollectible);

        NuGetExtensionLoader loader = new(NullSp);
        ParsedExtensionSource source = new() { Kind = "assembly", Value = dllPath };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.False(result.Name.StartsWith("__error__", StringComparison.Ordinal),
            $"Load failed: {result.Description}");
        Assert.NotEmpty(result.Tools);

        // Locate the emitted assembly by name among all loaded assemblies.
        Assembly? emittedAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, extName, StringComparison.Ordinal));

        Assert.NotNull(emittedAsm);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(emittedAsm));

        int collectibleAfter = AssemblyLoadContext.All.Count(c => c.IsCollectible);
        Assert.Equal(collectibleBefore, collectibleAfter);
    }

    /// <summary>
    /// Spec: "Loading a second extension does not disturb the first" —
    /// extension A's tools remain registered and callable after extension B is loaded.
    /// No Unload() is implied by loading B; no ALC is closed or replaced.
    /// Replaces the stale LoadAsync_ClosesPreviousALC_OnNextLoad test.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SecondExtension_DoesNotDisturbFirstExtensionsTools()
    {
        string guid = Guid.NewGuid().ToString("N");
        string extAName = $"ExtA{guid}";
        string extBName = $"ExtB{guid}";
        string extDir = Path.Combine(_tempDir, "twoext");
        Directory.CreateDirectory(extDir);

        // Emit extension A.
        string dllA = Path.Combine(extDir, extAName + ".dll");
        TestAssemblyEmitter.EmitAssembly(
            assemblyName: extAName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{extAName}}Extension : IDmonExtension
                {
                    public string Name => "{{extAName}}";
                    public string Description => "ext A";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "from A", "{{extAName}}_Tool", "tool from A")];
                }
                """,
            outputPath: dllA,
            additionalRefs: []);

        // Emit extension B.
        string dllB = Path.Combine(extDir, extBName + ".dll");
        TestAssemblyEmitter.EmitAssembly(
            assemblyName: extBName,
            source: $$"""
                using System.Collections.Generic;
                using Dmon.Extensions;
                using Microsoft.Extensions.AI;

                public sealed class {{extBName}}Extension : IDmonExtension
                {
                    public string Name => "{{extBName}}";
                    public string Description => "ext B";
                    public IEnumerable<AIFunction> Tools =>
                        [AIFunctionFactory.Create(() => "from B", "{{extBName}}_Tool", "tool from B")];
                }
                """,
            outputPath: dllB,
            additionalRefs: []);

        IToolRegistry registry = new ToolRegistry();
        NuGetExtensionLoader loader = new(NullSp);
        ExtensionService service = new(registry, [loader], NullLogger<ExtensionService>.Instance);

        // Load A, then B.
        await service.LoadAsync(dllA);
        await service.LoadAsync(dllB);

        // A's tools must still be in the registry after B was loaded.
        IReadOnlyList<RegisteredExtensionSnapshot> snapshot = registry.GetSnapshot();
        Assert.Contains(snapshot, s => s.Name == extAName);
        Assert.Contains(snapshot, s => s.Name == extBName);

        // A's AIFunction must still be invocable (not orphaned or removed).
        IReadOnlyList<AIFunction> allTools = registry.GetAll();
        AIFunction? aToolFn = allTools.FirstOrDefault(f => f.Name == $"{extAName}_Tool");
        Assert.NotNull(aToolFn);
        object? callResult = await aToolFn!.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        Assert.Equal("from A", callResult?.ToString());
    }

    [Fact]
    public void SourceKind_ReturnsNuget()
    {
        NuGetExtensionLoader loader = new(NullSp);

        Assert.Equal("nuget", loader.SourceKind);
    }
}

file sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
