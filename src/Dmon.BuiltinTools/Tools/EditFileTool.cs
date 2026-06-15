using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tools;

public sealed class EditFileTool : IToolExtension
{
    private readonly AIFunction _function;

    public EditFileTool()
    {
        _function = AIFunctionFactory.Create(
            (string path, string oldString, string newString) =>
            {
                try
                {
                    string resolved = Path.GetFullPath(path);
                    string text = File.ReadAllText(resolved);
                    int idx = text.IndexOf(oldString, StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        return $"Error: old_string not found in {path}";
                    }
                    string updated = string.Concat(
                        text.AsSpan(0, idx),
                        newString,
                        text.AsSpan(idx + oldString.Length));
                    File.WriteAllText(resolved, updated);
                    return "OK";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            },
            "edit_file",
            "Replace the first occurrence of old_string with new_string in a file.");
    }

    public string Name => "Edit File Tool";
    public string Description => "Replace text in an existing file.";
    public IEnumerable<AIFunction> Tools => [_function];

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
