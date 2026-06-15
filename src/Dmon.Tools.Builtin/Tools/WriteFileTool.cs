using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tools;

public sealed class WriteFileTool : IToolExtension
{
    private readonly AIFunction _function;

    public WriteFileTool()
    {
        _function = AIFunctionFactory.Create(
            ExecuteAsync,
            "write_file",
            "Write content to a file, creating it or overwriting if it exists.");
    }

    public string Name => "Write File Tool";
    public string Description => "Write or overwrite a file on disk.";
    public IEnumerable<AIFunction> Tools => [_function];

    private static async Task<string> ExecuteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string resolved = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(resolved);
            if (dir is not null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(resolved, content, cancellationToken);
            return "OK";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Prompt;

    public ToolConfirmRequest CreateConfirmRequest(FunctionCallContent call)
        => new()
        {
            Id = call.CallId,
            Name = call.Name,
            Args = call.Arguments is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(call.Arguments),
            Risk = RiskLevel.High
        };
}
