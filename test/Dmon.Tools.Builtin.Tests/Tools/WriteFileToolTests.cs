using Dmon.Tools.Builtin.Tools;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tests.Tools;

public sealed class WriteFileToolTests
{
    private static AIFunction GetFunction(WriteFileTool tool)
        => tool.Tools.Single();

    [Fact]
    public async Task Execute_WritesFileAndReturnsOk()
    {
        WriteFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string path = Path.GetTempFileName();
        try
        {
            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = path, ["content"] = "hello" }),
                CancellationToken.None);

            Assert.Equal("OK", result?.ToString());
            Assert.Equal("hello", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Execute_OverwritesExistingFile()
    {
        WriteFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "original");

            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = path, ["content"] = "updated" }),
                CancellationToken.None);

            Assert.Equal("OK", result?.ToString());
            Assert.Equal("updated", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Execute_InvalidPath_ReturnsErrorString()
    {
        WriteFileTool tool = new();
        AIFunction fn = GetFunction(tool);

        // Null bytes in path are invalid on all platforms.
        string invalidPath = "/\0invalid";

        object? result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = invalidPath, ["content"] = "x" }),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("Error:", result!.ToString());
    }

    [Fact]
    public async Task Execute_CreatesParentDirectories()
    {
        WriteFileTool tool = new();
        AIFunction fn = GetFunction(tool);
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string path = Path.Combine(dir, "sub", "file.txt");
        try
        {
            object? result = await fn.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = path, ["content"] = "data" }),
                CancellationToken.None);

            Assert.Equal("OK", result?.ToString());
            Assert.Equal("data", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
