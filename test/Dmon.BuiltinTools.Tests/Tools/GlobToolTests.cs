using Dmon.BuiltinTools.Tools;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tests.Tools;

[Collection("CwdMutating")]
public sealed class GlobToolTests
{
    private static AIFunction GetFunction(GlobTool tool)
        => tool.Tools.Single();

    [Fact]
    public async Task Execute_PatternWithMatches_ReturnsNewlineSeparatedPaths()
    {
        GlobTool tool = new();
        AIFunction fn = GetFunction(tool);

        // Create a temp directory with known files, then cd into it.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string original = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(dir, "alpha.cs"), "");
            File.WriteAllText(Path.Combine(dir, "beta.cs"), "");
            File.WriteAllText(Path.Combine(dir, "gamma.txt"), "");
            Environment.CurrentDirectory = dir;

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["pattern"] = "*.cs" }),
                CancellationToken.None);

            string output = result?.ToString() ?? "";
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Contains("alpha.cs", lines);
            Assert.Contains("beta.cs", lines);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_PatternWithNoMatches_ReturnsEmptyString()
    {
        GlobTool tool = new();
        AIFunction fn = GetFunction(tool);

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string original = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(dir, "hello.txt"), "");
            Environment.CurrentDirectory = dir;

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["pattern"] = "*.cs" }),
                CancellationToken.None);

            Assert.Equal(string.Empty, result?.ToString() ?? string.Empty);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_DoubleStarPattern_MatchesNestedFiles()
    {
        GlobTool tool = new();
        AIFunction fn = GetFunction(tool);

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string original = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(sub, "nested.cs"), "");
            Environment.CurrentDirectory = dir;

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }),
                CancellationToken.None);

            string output = result?.ToString() ?? "";
            Assert.Contains("nested.cs", output);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(dir, recursive: true);
        }
    }
}
