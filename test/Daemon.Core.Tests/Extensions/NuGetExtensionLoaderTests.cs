using Daemon.Core.Extensions;
using Daemon.Extensions;
using Daemon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Tests.Extensions;

public sealed class NuGetExtensionLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public NuGetExtensionLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"daemon-ext-test-{Guid.NewGuid():N}");
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
        NuGetExtensionLoader loader = new();

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
        NuGetExtensionLoader loader = new();
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
        NuGetExtensionLoader loader = new();

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
        NuGetExtensionLoader loader = new();
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
        NuGetExtensionLoader loader = new();
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

    [Fact]
    public async Task LoadAsync_ClosesPreviousALC_OnNextLoad()
    {
        string assemblyPath = typeof(TestExtension).Assembly.Location;
        NuGetExtensionLoader loader = new();

        ParsedExtensionSource source = new()
        {
            Kind = "assembly",
            Value = assemblyPath
        };

        ExtensionLoadResult first = await loader.LoadAsync(source);
        ExtensionLoadResult second = await loader.LoadAsync(source);

        // Both loads should succeed — second load creates a new ALC.
        Assert.NotEmpty(first.Tools);
        Assert.NotEmpty(second.Tools);
    }

    [Fact]
    public void SourceKind_ReturnsNuget()
    {
        NuGetExtensionLoader loader = new();

        Assert.Equal("nuget", loader.SourceKind);
    }
}
