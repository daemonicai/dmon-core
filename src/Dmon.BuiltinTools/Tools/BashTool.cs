using System.Diagnostics;
using Dmon.BuiltinTools.Bash;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Models;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.BuiltinTools.Tools;

public sealed class BashTool : IToolExtension
{
    private readonly int _timeoutSeconds;
    private readonly IDenylistChecker _denylist;
    private readonly IBashCompositeDetector _compositeDetector;
    private readonly AIFunction _function;

    public BashTool(
        IDenylistChecker denylist,
        IBashCompositeDetector compositeDetector,
        int timeoutSeconds = 30)
    {
        _denylist = denylist;
        _compositeDetector = compositeDetector;
        _timeoutSeconds = timeoutSeconds;
        _function = AIFunctionFactory.Create(
            ExecuteAsync,
            "bash",
            "Execute a shell command and return combined stdout+stderr output.");
    }

    public string Name => "Bash Tool";
    public string Description => "Shell command execution.";
    public IEnumerable<AIFunction> Tools => [_function];

    private async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        bool isWindows = OperatingSystem.IsWindows();
        string shell = isWindows ? "cmd.exe" : "/bin/sh";
        string[] shellArgs = isWindows ? ["/c", command] : ["-c", command];

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (string arg in shellArgs)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(_timeoutSeconds));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, cancellationToken);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string combined = stdout + stderr;

            return process.ExitCode == 0
                ? combined
                : $"Exit {process.ExitCode}: {combined}";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            return "Error: timed out";
        }
    }

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue("command", out object? cmdArg))
            return PermissionResult.Prompt;

        string command = cmdArg?.ToString() ?? string.Empty;

        if (_denylist.IsDenied(command))
            return PermissionResult.Deny;

        if (_compositeDetector.IsComposite(command))
            return PermissionResult.Prompt;

        if (MatchesBashGlob(command, project.Settings.Bash.Deny))
            return PermissionResult.Deny;
        if (global is not null && MatchesBashGlob(command, global.Settings.Bash.Deny))
            return PermissionResult.Deny;
        if (MatchesBashGlob(command, project.Settings.Bash.Allow))
            return PermissionResult.Allow;
        if (global is not null && MatchesBashGlob(command, global.Settings.Bash.Allow))
            return PermissionResult.Allow;

        return PermissionResult.Prompt;
    }

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

    private static bool MatchesBashGlob(string command, IReadOnlyList<string> patterns)
    {
        foreach (string pattern in patterns)
        {
            if (SimpleGlobMatch(command, pattern)) return true;
        }
        return false;
    }

    private static bool SimpleGlobMatch(string input, string pattern)
        => GlobMatchSpan(input.AsSpan(), pattern.AsSpan());

    private static bool GlobMatchSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        while (!pattern.IsEmpty)
        {
            if (pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.IsEmpty) return true;
                for (int i = 0; i <= input.Length; i++)
                {
                    if (GlobMatchSpan(input[i..], pattern)) return true;
                }
                return false;
            }
            if (input.IsEmpty || input[0] != pattern[0]) return false;
            input = input[1..];
            pattern = pattern[1..];
        }
        return input.IsEmpty;
    }
}
