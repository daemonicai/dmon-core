using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tools;

public sealed class ReadFileTool : IToolExtension
{
    private readonly AIFunction _function;

    public ReadFileTool()
    {
        _function = AIFunctionFactory.Create(
            (string path) =>
            {
                try
                {
                    string resolved = Path.GetFullPath(path);
                    return File.ReadAllText(resolved);
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            },
            "read_file",
            "Read the full text content of a file at the given path.");
    }

    public string Name => "Read File Tool";
    public string Description => "Read file contents from disk.";
    public IEnumerable<AIFunction> Tools => [_function];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue("path", out object? pathArg))
            return PermissionResult.Prompt;

        string path = pathArg?.ToString() ?? string.Empty;

        try
        {
            // Resolve BOTH the target and the CWD through symlinks before the containment
            // check. Path.GetFullPath collapses ".." but does not follow symlinks, so a
            // symlink inside the CWD pointing outside would otherwise be auto-allowed.
            // Resolving the CWD too avoids false prompts where the CWD itself lives under a
            // symlinked ancestor (e.g. macOS /tmp -> /private/tmp). Fail closed on either
            // being unresolvable (a broken/dangling symlink).
            string? resolved = RealPathResolver.ResolveRealPath(Path.GetFullPath(path));
            string? cwd = RealPathResolver.ResolveRealPath(Path.GetFullPath(Environment.CurrentDirectory));
            if (resolved is null || cwd is null)
                return PermissionResult.Prompt;

            return IsUnder(resolved, cwd) ? PermissionResult.Allow : PermissionResult.Prompt;
        }
        catch
        {
            return PermissionResult.Prompt;
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        string dir = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(dir, StringComparison.Ordinal)
            || string.Equals(path, directory, StringComparison.Ordinal);
    }
}
