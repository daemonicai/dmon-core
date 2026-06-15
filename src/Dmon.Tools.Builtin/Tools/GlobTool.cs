using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Builtin.Tools;

public sealed class GlobTool : IToolExtension
{
    private readonly AIFunction _function;

    public GlobTool()
    {
        _function = AIFunctionFactory.Create(
            (string pattern) =>
            {
                string cwd = Environment.CurrentDirectory;
                IEnumerable<string> files = Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories);
                List<string> matches = [];

                foreach (string file in files)
                {
                    string relative = Path.GetRelativePath(cwd, file);
                    if (MatchGlobPattern(pattern, relative))
                    {
                        matches.Add(relative);
                    }
                }

                return string.Join("\n", matches);
            },
            "glob",
            "Return newline-separated file paths matching the given glob pattern, relative to the current directory.");
    }

    public string Name => "Glob Tool";
    public string Description => "Find files matching a glob pattern within the current directory.";
    public IEnumerable<AIFunction> Tools => [_function];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow;

    private static bool MatchGlobPattern(string pattern, string path)
    {
        string normPath = path.Replace(Path.DirectorySeparatorChar, '/');
        string normPattern = pattern.Replace('\\', '/');
        return GlobMatch(normPattern.AsSpan(), normPath.AsSpan());
    }

    private static bool GlobMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> path)
    {
        while (!pattern.IsEmpty && !path.IsEmpty)
        {
            if (pattern.StartsWith("**/".AsSpan()))
            {
                ReadOnlySpan<char> rest = pattern[3..];
                if (GlobMatch(rest, path)) return true;
                int slash = path.IndexOf('/');
                while (slash >= 0)
                {
                    path = path[(slash + 1)..];
                    if (GlobMatch(rest, path)) return true;
                    slash = path.IndexOf('/');
                }
                return false;
            }
            if (pattern[0] == '*')
            {
                ReadOnlySpan<char> rest = pattern[1..];
                for (int i = 0; i <= path.Length; i++)
                {
                    if (i < path.Length && path[i] == '/') break;
                    if (GlobMatch(rest, path[i..])) return true;
                }
                return false;
            }
            if (pattern[0] != path[0]) return false;
            pattern = pattern[1..];
            path = path[1..];
        }
        return pattern.IsEmpty && path.IsEmpty;
    }
}
