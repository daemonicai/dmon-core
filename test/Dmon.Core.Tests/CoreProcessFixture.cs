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

    /// <summary>
    /// Per-fixture content root the core runs in. Each fixture instance gets its
    /// own temp directory so parallel core-launching fixtures never share a mutable
    /// <c>appsettings.json</c> — a shared file is read by one booting core while a
    /// sibling fixture truncates it mid-write, crashing the reader.
    /// </summary>
    public string? CoreDir { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public async Task InitializeAsync()
    {
        (string coreDll, _) = FindCoreDll();

        string contentRoot = Path.Combine(Path.GetTempPath(), $"dmon-core-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        CoreDir = contentRoot;

        string appSettingsPath = Path.Combine(contentRoot, "appsettings.json");
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

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{coreDll}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = contentRoot
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

        if (CoreDir is not null && Directory.Exists(CoreDir))
        {
            try { Directory.Delete(CoreDir, recursive: true); }
            catch { /* best effort */ }
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

        // Prebuilt default-core closure produced by `make build-core`
        // (publish of default-core/Dmon.cs into build/dmoncore/).
        string dll = Path.Combine(repoRoot, "build/dmoncore/dmoncore.dll");
        if (File.Exists(dll))
            return (dll, Path.GetDirectoryName(dll)!);

        throw new FileNotFoundException(
            "Could not find build/dmoncore/dmoncore.dll. Run 'make build-core' first.",
            "dmoncore.dll");
    }
}
