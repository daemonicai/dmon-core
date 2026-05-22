using Daemon.Core.Extensions;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Tests.Extensions;

public sealed class CsxScriptLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public CsxScriptLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"daemon-csx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteScriptFile(string fileName, string content)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task LoadAsync_ScriptReturnsAIFunction_LoadsSuccessfully()
    {
        string scriptPath = WriteScriptFile("test.csx", """
            #r "nuget: Microsoft.Extensions.AI, 10.6.0"

            using Microsoft.Extensions.AI;

            var fn = AIFunctionFactory.Create(
                () => "Hello from csx!",
                "CsxHello",
                "Returns a greeting from a script.");

            return fn;
            """);

        CsxScriptLoader loader = new();
        loader.ConfirmCallback = AllowAll;

        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.Equal("test", result.Name);
        Assert.Single(result.Tools);
        Assert.Equal("CsxHello", result.Tools[0].Name);
        Assert.Equal("script", result.SourceKind);
    }

    [Fact]
    public async Task LoadAsync_ScriptReturnsMultipleFunctions_LoadsAll()
    {
        string scriptPath = WriteScriptFile("multi.csx", """
            #r "nuget: Microsoft.Extensions.AI, 10.6.0"

            using Microsoft.Extensions.AI;

            var fn1 = AIFunctionFactory.Create(
                () => "one",
                "FunctionOne",
                "First function.");

            var fn2 = AIFunctionFactory.Create(
                () => "two",
                "FunctionTwo",
                "Second function.");

            return new AIFunction[] { fn1, fn2 };
            """);

        CsxScriptLoader loader = new();
        loader.ConfirmCallback = AllowAll;

        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.Equal("multi", result.Name);
        Assert.Equal(2, result.Tools.Count);
        Assert.Contains(result.Tools, f => f.Name == "FunctionOne");
        Assert.Contains(result.Tools, f => f.Name == "FunctionTwo");
    }

    [Fact]
    public async Task LoadAsync_ScriptNotFound_ReturnsErrorResult()
    {
        CsxScriptLoader loader = new();
        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = "/nonexistent/script.csx"
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.StartsWith("__error__", result.Name);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task LoadAsync_ScriptReturnsNull_ReturnsErrorResult()
    {
        string scriptPath = WriteScriptFile("nullret.csx", """
            return null;
            """);

        CsxScriptLoader loader = new();
        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.StartsWith("__error__", result.Name);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task LoadAsync_ScriptWithNoFunctions_ReturnsErrorResult()
    {
        string scriptPath = WriteScriptFile("stringret.csx", """
            // This script returns a string, not an AIFunction
            var x = "hello";
            return x;
            """);

        CsxScriptLoader loader = new();
        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.StartsWith("__error__", result.Name);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task LoadAsync_ConfirmCallbackDenied_ReturnsErrorResult()
    {
        string scriptPath = WriteScriptFile("blocked.csx", """
            return "hello";
            """);

        CsxScriptLoader loader = new();
        loader.ConfirmCallback = (_, _) => Task.FromResult(false);

        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.StartsWith("__error__", result.Name);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task LoadAsync_ScriptWithNuGetDirectives_CallsConfirmCallback()
    {
        string scriptPath = WriteScriptFile("withnuget.csx", """
            #r "nuget: Newtonsoft.Json, 13.0.3"
            return "hello";
            """);

        CsxScriptLoader loader = new();
        List<ExtensionLoadConfirmRequest> calls = [];
        loader.ConfirmCallback = (request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(false); // Deny so we don't try NuGet restore.
        };

        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        await loader.LoadAsync(source);

        Assert.Contains(calls, c =>
            c.Phase == "resolve"
            && c.Description.Contains("Newtonsoft.Json"));
    }

    [Fact]
    public void SourceKind_ReturnsScript()
    {
        CsxScriptLoader loader = new();

        Assert.Equal("script", loader.SourceKind);
    }

    [Fact]
    public async Task LoadAsync_Name_DerivedFromFileName()
    {
        string scriptPath = WriteScriptFile("MyCoolExtension.csx", """
            #r "nuget: Microsoft.Extensions.AI, 10.6.0"

            using Microsoft.Extensions.AI;

            return AIFunctionFactory.Create(
                () => "x",
                "SomeFunc",
                "desc");
            """);

        CsxScriptLoader loader = new();
        loader.ConfirmCallback = AllowAll;

        ParsedExtensionSource source = new()
        {
            Kind = "script",
            Value = scriptPath
        };

        ExtensionLoadResult result = await loader.LoadAsync(source);

        Assert.Equal("MyCoolExtension", result.Name);
    }

    private static readonly Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>> AllowAll =
        (_, _) => Task.FromResult(true);
}
