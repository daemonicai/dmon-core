using Dmon.Tools.Builtin.Tools;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tests.Tools;

public sealed class EditFileToolTests
{
    private static AIFunction GetFunction(EditFileTool tool)
        => tool.Tools.Single();

    [Fact]
    public async Task Execute_ReplacesFirstOccurrence()
    {
        EditFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "foo bar foo");

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["oldString"] = "foo",
                    ["newString"] = "baz"
                }),
                CancellationToken.None);

            Assert.Equal("OK", result?.ToString());
            Assert.Equal("baz bar foo", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Execute_OldStringNotFound_ReturnsErrorString()
    {
        EditFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "hello world");

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["oldString"] = "nonexistent",
                    ["newString"] = "replacement"
                }),
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.StartsWith("Error:", result!.ToString());
            Assert.Contains("old_string not found", result.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Execute_FileMissing_ReturnsErrorString()
    {
        EditFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");

        object? result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["oldString"] = "x",
                ["newString"] = "y"
            }),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("Error:", result!.ToString());
    }
}
