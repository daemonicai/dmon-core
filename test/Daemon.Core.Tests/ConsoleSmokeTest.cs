using System.Diagnostics;
using System.Text.Json;

namespace Daemon.Core.Tests;

/// <summary>
/// End-to-end smoke test that launches the Daemon.Core process over stdio
/// and verifies it starts, responds to commands, and shuts down cleanly.
///
/// Requires a valid provider config in an appsettings.json next to the core DLL,
/// or a pre-configured .daemon/config.yaml at CWD. The test auto-creates
/// a minimal appsettings.json before launching the core.
/// </summary>
public class ConsoleSmokeTest : IAsyncLifetime
{
    private Process? _coreProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _tempWorkDir;
    private readonly List<string> _coreStderr = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InitializeAsync()
    {
        (string coreDll, string coreDir) = FindCoreDll();

        // Write a minimal appsettings.json next to the core DLL so the host finds it.
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

        // Use the core dir as working directory so bootstrap creates .daemon/ there.
        _tempWorkDir = coreDir;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{coreDll}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _tempWorkDir
        };

        _coreProcess = new Process { StartInfo = psi };
        _coreProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _coreStderr.Add(e.Data);
                System.Diagnostics.Debug.WriteLine($"[core-stderr] {e.Data}");
            }
        };

        _coreProcess.Start();
        _coreProcess.BeginErrorReadLine();
        _stdin = _coreProcess.StandardInput;
        _stdout = _coreProcess.StandardOutput;
    }

    public async Task DisposeAsync()
    {
        if (_coreProcess is { HasExited: false })
        {
            try
            {
                _stdin?.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _coreProcess.WaitForExitAsync(cts.Token);
            }
            catch
            {
                try { _coreProcess.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }
        }
        _coreProcess?.Dispose();
    }

    [Fact]
    public async Task CoreStartsAndRespondsToSessionList()
    {
        Assert.NotNull(_stdout);

        // Wait for agentReady
        string? readyLine = await _stdout.ReadLineAsync();
        if (readyLine is null)
        {
            string stderrText = string.Join("\n", _coreStderr);
            string extraInfo = _coreProcess?.HasExited == true
                ? $"Core exited with code {_coreProcess.ExitCode}"
                : "Core still running";
            Assert.Fail($"agentReady was null. {extraInfo}. Core stderr:\n{stderrText}");
        }

        JsonDocument readyDoc = JsonDocument.Parse(readyLine);
        Assert.Equal("agentReady", readyDoc.RootElement.GetProperty("type").GetString());
        Assert.NotNull(readyDoc.RootElement.GetProperty("protocolVersion").GetString());
        Assert.NotNull(readyDoc.RootElement.GetProperty("coreVersion").GetString());

        // Send session.list
        string cmdId = Guid.NewGuid().ToString("N");
        string cmd = JsonSerializer.Serialize(new { type = "session.list", id = cmdId }, JsonOptions);
        await _stdin!.WriteLineAsync(cmd);
        await _stdin.FlushAsync();

        // Read response — may take a few lines if there are other events
        string? respLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await _stdout.ReadLineAsync();
            Assert.NotNull(line);
            if (line!.Contains("\"response\"") && line.Contains(cmdId))
            {
                respLine = line;
                break;
            }
        }

        Assert.NotNull(respLine);
        JsonDocument respDoc = JsonDocument.Parse(respLine);
        Assert.Equal("response", respDoc.RootElement.GetProperty("type").GetString());
        Assert.True(respDoc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task CoreRespondsToErrorOnMalformedCommand()
    {
        Assert.NotNull(_stdout);

        // Wait for agentReady
        string? readyLine = await _stdout.ReadLineAsync();
        if (readyLine is null)
        {
            string stderrText = string.Join("\n", _coreStderr);
            string extraInfo = _coreProcess?.HasExited == true
                ? $"Core exited with code {_coreProcess.ExitCode}"
                : "Core still running";
            Assert.Fail($"agentReady was null. {extraInfo}. Core stderr:\n{stderrText}");
        }

        Assert.Contains("agentReady", readyLine);

        // Send garbage
        await _stdin!.WriteLineAsync("{not valid json");
        await _stdin.FlushAsync();

        // Read error event
        string? errorLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await _stdout.ReadLineAsync();
            if (line is null) break;
            if (line.Contains("\"error\""))
            {
                errorLine = line;
                break;
            }
        }

        Assert.NotNull(errorLine);
        JsonDocument errorDoc = JsonDocument.Parse(errorLine);
        Assert.Equal("error", errorDoc.RootElement.GetProperty("type").GetString());
    }

    private static (string path, string directory) FindCoreDll()
    {
        string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";

        // Walk from test DLL to repo root.
        // Test DLL is at test/Daemon.Core.Tests/bin/Debug/net10.0/
        // Going up 5 levels reaches the repo root.
        string repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "../../../../.."));

        string[] candidates =
        [
            Path.Combine(repoRoot, "src/Daemon.Core/bin/Debug/net10.0/Daemon.Core.dll"),
            Path.Combine(repoRoot, "src/Daemon.Core/bin/Release/net10.0/Daemon.Core.dll"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return (candidate, Path.GetDirectoryName(candidate)!);
            }
        }

        throw new FileNotFoundException(
            $"Could not find Daemon.Core.dll. Run 'dotnet build' first.",
            "Daemon.Core.dll");
    }
}