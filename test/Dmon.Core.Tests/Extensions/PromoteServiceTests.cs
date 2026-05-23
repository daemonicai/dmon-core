using Dmon.Core.Extensions;

namespace Dmon.Core.Tests.Extensions;

public sealed class PromoteServiceTests : IDisposable
{
    private readonly string _tempDir;

    public PromoteServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-promote-test-{Guid.NewGuid():N}");
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
    public async Task PromoteAsync_ScaffoldsCsAndCsproj()
    {
        string scriptPath = WriteScriptFile("myext.csx", """
            #r "nuget: Newtonsoft.Json, 13.0.3"
            using Newtonsoft.Json;
            using Microsoft.Extensions.AI;

            AIFunction fn = AIFunctionFactory.Create(
                () => "hello",
                "MyFunc",
                "A simple function.");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        Assert.Equal("myextExtension", result.ClassName);
        Assert.True(File.Exists(result.ClassFilePath));
        Assert.True(File.Exists(result.CsprojFilePath));

        // Verify #r extraction.
        Assert.Single(result.PackageReferences);
        Assert.Equal("Newtonsoft.Json", result.PackageReferences[0].PackageId);
        Assert.Equal("13.0.3", result.PackageReferences[0].Version);
    }

    [Fact]
    public async Task PromoteAsync_CustomClassName()
    {
        string scriptPath = WriteScriptFile("test.csx", """
            var fn = AIFunctionFactory.Create(() => 42, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output2");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir, "MyNamedExtension");

        Assert.Equal("MyNamedExtension", result.ClassName);
        Assert.True(File.Exists(result.ClassFilePath));
    }

    [Fact]
    public async Task PromoteAsync_MissingScript_Throws()
    {
        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output3");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.PromoteAsync("/nonexistent.csx", outputDir));
    }

    [Fact]
    public async Task PromoteAsync_MultipleNuGetReferences_ExtractsAll()
    {
        string scriptPath = WriteScriptFile("manyr.csx", """
            #r "nuget: FirstPackage, 1.0.0"
            #r "nuget: SecondPackage, 2.0.0"
            #r "nuget: ThirdPackage"
            var fn = AIFunctionFactory.Create(() => 42, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output4");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        Assert.Equal(3, result.PackageReferences.Count);
        Assert.Contains(result.PackageReferences, p => p.PackageId == "FirstPackage" && p.Version == "1.0.0");
        Assert.Contains(result.PackageReferences, p => p.PackageId == "SecondPackage" && p.Version == "2.0.0");
        Assert.Contains(result.PackageReferences, p => p.PackageId == "ThirdPackage" && p.Version == null);
    }

    [Fact]
    public async Task PromoteAsync_CsprojContainsRequiredReferences()
    {
        string scriptPath = WriteScriptFile("refs.csx", """
            var fn = AIFunctionFactory.Create(() => 1, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output5");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        string csprojContent = await File.ReadAllTextAsync(result.CsprojFilePath);

        Assert.Contains("Microsoft.Extensions.AI", csprojContent);
        Assert.Contains("Dmon.Extensions", csprojContent);
    }

    [Fact]
    public async Task PromoteAsync_GeneratedClassImplementsIDmonExtension()
    {
        string scriptPath = WriteScriptFile("iface.csx", """
            var fn = AIFunctionFactory.Create(() => 1, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output6");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        string classContent = await File.ReadAllTextAsync(result.ClassFilePath);

        Assert.Contains("IDmonExtension", classContent);
        Assert.Contains("public string Name", classContent);
        Assert.Contains("public string Description", classContent);
        Assert.Contains("IEnumerable<AIFunction> Tools", classContent);
    }

    [Fact]
    public async Task PromoteAsync_OutputDirCreated_IfNotExists()
    {
        string scriptPath = WriteScriptFile("dir.csx", """
            var fn = AIFunctionFactory.Create(() => 1, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "nested", "output");

        // outputDir should not exist before.
        Assert.False(Directory.Exists(outputDir));

        await service.PromoteAsync(scriptPath, outputDir);

        Assert.True(Directory.Exists(outputDir));
        Assert.True(File.Exists(Path.Combine(outputDir, "dirExtension.cs")));
    }

    [Fact]
    public async Task PromoteAsync_NoNuGetReferences_ReturnsEmptyList()
    {
        string scriptPath = WriteScriptFile("nopkg.csx", """
            var fn = AIFunctionFactory.Create(() => 1, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output8");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        Assert.Empty(result.PackageReferences);
    }

    [Fact]
    public async Task PromoteAsync_NonNuGetRDirectives_InOtherDirectives()
    {
        string scriptPath = WriteScriptFile("otherr.csx", """
            #r "System.Text.Json"
            #r "nuget: SomePackage, 1.0"
            var fn = AIFunctionFactory.Create(() => 1, "Fn", "desc");
            return fn;
            """);

        PromoteService service = new();
        string outputDir = Path.Combine(_tempDir, "output9");

        PromoteResult result = await service.PromoteAsync(scriptPath, outputDir);

        Assert.Single(result.PackageReferences);
        Assert.NotEmpty(result.OtherRDirectives);
    }
}
