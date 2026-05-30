using System.Diagnostics;
using System.Reflection;

namespace Dmon.Core.Tests;

/// <summary>
/// xUnit class fixture that launches a single Dmon.Core process and waits for
/// <c>agentReady</c> before any test in the class runs. Shared across all tests
/// in a class via <see cref="IClassFixture{T}"/>, so the process is started and
/// stopped once per test class rather than once per test.
/// </summary>
public sealed class CoreProcessFixture : IAsyncLifetime
{
    private Process? _process;

    public StreamWriter? StandardInput { get; private set; }
    public StreamReader? StandardOutput { get; private set; }
    public List<string> Stderr { get; } = [];
    public bool AgentReadyReceived { get; private set; }
    public string? AgentReadyLine { get; private set; }
    public string? CoreDir { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public async Task InitializeAsync()
    {
        (string coreDll, string coreDir) = FindCoreDll();
        CoreDir = coreDir;

        string appSettingsPath = Path.Combine(coreDir, "appsettings.json");
        await File.WriteAllTextAsync(appSettingsPath, """
        {
          "providers": {
            "test": {
              "adapter": "openai",
              "defaultModelId": "gpt-4",
              "auth": { "type": "none" }
            }
          }
        }
        """);

        string dmonDir = Path.Combine(coreDir, ".dmon");
        if (Directory.Exists(dmonDir))
            Directory.Delete(dmonDir, recursive: true);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{coreDll}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = coreDir
        };

        _process = new Process { StartInfo = psi };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Stderr.Add(e.Data);
        };
        _process.Start();
        _process.BeginErrorReadLine();
        StandardInput = _process.StandardInput;
        StandardOutput = _process.StandardOutput;

        await WaitForAgentReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                StandardInput?.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cts.Token);
            }
            catch
            {
                try { _process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
        }

        _process?.Dispose();

        if (CoreDir is not null)
        {
            string dmonDir = Path.Combine(CoreDir, ".dmon");
            if (Directory.Exists(dmonDir))
            {
                try { Directory.Delete(dmonDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Reads one line from the core's stdout with a timeout. Returns <c>null</c>
    /// if the timeout elapses or the stream ends.
    /// </summary>
    public async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await StandardOutput!.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public string FormatFailure(string message)
    {
        string stderrText = string.Join("\n", Stderr);
        string extraInfo = _process?.HasExited == true
            ? $"Core exited with code {_process.ExitCode}"
            : "Core still running";
        return $"{message}. {extraInfo}. Core stderr:\n{stderrText}";
    }

    private async Task WaitForAgentReadyAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            string? line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10));
            if (line is null) break;
            if (line.Contains("\"agentReady\""))
            {
                AgentReadyReceived = true;
                AgentReadyLine = line;
                return;
            }
        }
    }

    internal static (string path, string directory) FindCoreDll()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));

        string[] candidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/dmoncore.dll"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/dmoncore.dll"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return (candidate, Path.GetDirectoryName(candidate)!);
        }

        throw new FileNotFoundException(
            "Could not find dmoncore.dll. Run 'dotnet build' first.",
            "dmoncore.dll");
    }
}
