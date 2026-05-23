using Dmon.BuiltinTools.Tools;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tests.Tools;

[Collection("CwdMutating")]
public sealed class ReadFileToolTests
{
    private static IPermissionSettings MakeSettings(PermissionSettings? settings = null)
        => new StubPermissionSettings(settings ?? new PermissionSettings());

    private sealed class StubPermissionSettings(PermissionSettings settings) : IPermissionSettings
    {
        public PermissionSettings Settings => settings;
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static FunctionCallContent MakeCall(string path)
        => new("call-1", "read_file", new Dictionary<string, object?> { ["path"] = path });

    [Fact]
    public void Evaluate_PathUnderCwd_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwdFile = Path.Combine(Environment.CurrentDirectory, "somefile.txt");
        FunctionCallContent call = MakeCall(cwdFile);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_PathOutsideCwd_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        string outsidePath = Path.Combine(Path.GetTempPath(), "outsidefile.txt");
        FunctionCallContent call = MakeCall(outsidePath);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_NullArguments_ReturnsPrompt()
    {
        ReadFileTool tool = new();
        FunctionCallContent call = new("call-1", "read_file", null);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_CwdItself_ReturnsAllow()
    {
        ReadFileTool tool = new();
        string cwd = Environment.CurrentDirectory;
        FunctionCallContent call = MakeCall(cwd);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }
}
