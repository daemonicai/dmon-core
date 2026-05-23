using Dmon.BuiltinTools.Bash;
using Dmon.BuiltinTools.Tools;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tests.Tools;

public sealed class BashToolTests
{
    // --- Test doubles ---

    private sealed class StubDenylist(bool deny) : IDenylistChecker
    {
        public bool IsDenied(string command) => deny;
    }

    private sealed class StubCompositeDetector(bool composite) : IBashCompositeDetector
    {
        public bool IsComposite(string command) => composite;
    }

    private static IPermissionSettings MakeSettings(IReadOnlyList<string>? bashAllow = null, IReadOnlyList<string>? bashDeny = null)
    {
        PermissionSettings settings = new()
        {
            Bash = new TierSettings
            {
                Allow = bashAllow ?? [],
                Deny = bashDeny ?? []
            }
        };
        return new StubPermissionSettings(settings);
    }

    private sealed class StubPermissionSettings(PermissionSettings settings) : IPermissionSettings
    {
        public PermissionSettings Settings => settings;
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static FunctionCallContent MakeCall(string command)
        => new("call-1", "bash", new Dictionary<string, object?> { ["command"] = command });

    private static BashTool MakeTool(
        bool deny = false,
        bool composite = false,
        int timeoutSeconds = 30)
        => new(new StubDenylist(deny), new StubCompositeDetector(composite), timeoutSeconds);

    private static AIFunction GetFunction(BashTool tool)
        => tool.Tools.Single();

    // --- Evaluate tests ---

    [Fact]
    public void Evaluate_DenylistedCommand_ReturnsDeny()
    {
        BashTool tool = MakeTool(deny: true);
        FunctionCallContent call = MakeCall("rm -rf /");

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Deny, result);
    }

    [Fact]
    public void Evaluate_CompositeCommand_ReturnsPrompt()
    {
        BashTool tool = MakeTool(deny: false, composite: true);
        FunctionCallContent call = MakeCall("ls && rm -rf /");

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_AllowedByProjectPattern_ReturnsAllow()
    {
        BashTool tool = MakeTool(deny: false, composite: false);
        FunctionCallContent call = MakeCall("git status");
        IPermissionSettings settings = MakeSettings(bashAllow: ["git *"]);

        PermissionResult result = tool.Evaluate(call, settings, null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_AllowedByGlobalPattern_ReturnsAllow()
    {
        BashTool tool = MakeTool(deny: false, composite: false);
        FunctionCallContent call = MakeCall("echo hello");
        IPermissionSettings projectSettings = MakeSettings();
        IPermissionSettings globalSettings = MakeSettings(bashAllow: ["echo *"]);

        PermissionResult result = tool.Evaluate(call, projectSettings, globalSettings);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_NoPattern_ReturnsPrompt()
    {
        BashTool tool = MakeTool(deny: false, composite: false);
        FunctionCallContent call = MakeCall("ls -la");

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    [Fact]
    public void Evaluate_NullArguments_ReturnsPrompt()
    {
        BashTool tool = MakeTool();
        FunctionCallContent call = new("call-1", "bash", null);

        PermissionResult result = tool.Evaluate(call, MakeSettings(), null);

        Assert.Equal(PermissionResult.Prompt, result);
    }

    // --- Execution tests ---

    [Fact]
    public async Task Execute_ExitZero_ReturnsOutput()
    {
        BashTool tool = MakeTool();
        AIFunction fn = GetFunction(tool);

        object? result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "echo hello" }),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("hello", result!.ToString());
    }

    [Fact]
    public async Task Execute_ExitNonZero_ReturnsPrefixedError()
    {
        BashTool tool = MakeTool();
        AIFunction fn = GetFunction(tool);

        object? result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "exit 2" }),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("Exit 2:", result!.ToString());
    }

    [Fact]
    public async Task Execute_Timeout_ReturnsTimedOutError()
    {
        BashTool tool = MakeTool(timeoutSeconds: 1);
        AIFunction fn = GetFunction(tool);

        object? result = await fn.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["command"] = "sleep 10" }),
            CancellationToken.None);

        Assert.Equal("Error: timed out", result?.ToString());
    }
}
