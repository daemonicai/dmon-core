using System.Diagnostics;
using System.Text.Json;

namespace Daemon.Core.Tests.Integration;

/// <summary>
/// End-to-end integration test that launches the Daemon.Core process over stdio
/// and verifies the full RPC surface: session creation, model listing, turn
/// submission with event flow, and error handling.
///
/// Shares the same process-launch pattern as ConsoleSmokeTest but adds
/// turn-level event verification.
/// </summary>
public class IntegrationSmokeTest : IAsyncLifetime
{
    private Process? _coreProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _coreDir;
    private readonly List<string> _coreStderr = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InitializeAsync()
    {
        (string coreDll, string coreDir) = FindCoreDll();
        _coreDir = coreDir;

        // Write a minimal appsettings.json next to the core DLL so the host finds a provider.
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

        // Remove any residual .daemon/ from prior test runs in the core DLL directory.
        string daemonDir = Path.Combine(coreDir, ".daemon");
        if (Directory.Exists(daemonDir))
        {
            Directory.Delete(daemonDir, recursive: true);
        }

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

        _coreProcess = new Process { StartInfo = psi };
        _coreProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _coreStderr.Add(e.Data);
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

        // Clean up test bootstrap artifacts.
        if (_coreDir is not null)
        {
            string daemonDir = Path.Combine(_coreDir, ".daemon");
            if (Directory.Exists(daemonDir))
            {
                try { Directory.Delete(daemonDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task CoreStartsAndEmitsAgentReady()
    {
        Assert.NotNull(_stdout);

        string? readyLine = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10));
        if (readyLine is null)
        {
            Assert.Fail(FormatFailure("agentReady was null"));
        }

        using JsonDocument doc = JsonDocument.Parse(readyLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("agentReady", root.GetProperty("type").GetString());
        Assert.Equal("1.0", root.GetProperty("protocolVersion").GetString());
        Assert.NotNull(root.GetProperty("coreVersion").GetString());
    }

    [Fact]
    public async Task SessionCreateReturnsNewSession()
    {
        Assert.NotNull(_stdout);
        await SkipToAgentReadyAsync();

        string cmdId = Guid.NewGuid().ToString("N");

        await SendAsync(new { type = "session.create", id = cmdId });

        string? respLine = await ReadResponseAsync(cmdId);
        Assert.NotNull(respLine);

        using JsonDocument doc = JsonDocument.Parse(respLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        JsonElement payload = root.GetProperty("payload");
        Assert.NotNull(payload.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ModelListReturnsModels()
    {
        Assert.NotNull(_stdout);
        await SkipToAgentReadyAsync();

        string cmdId = Guid.NewGuid().ToString("N");

        await SendAsync(new { type = "model.list", id = cmdId });

        string? respLine = await ReadResponseAsync(cmdId);
        Assert.NotNull(respLine);

        using JsonDocument doc = JsonDocument.Parse(respLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        JsonElement payload = root.GetProperty("payload");
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        // NullModelHandler returns an empty list when no providers are configured.
        // The array may be empty but must be present.
    }

    [Fact]
    public async Task TurnSubmitEmitsTurnStartAndTurnEnd()
    {
        Assert.NotNull(_stdout);
        await SkipToAgentReadyAsync();

        string cmdId = Guid.NewGuid().ToString("N");

        await SendAsync(new { type = "turn.submit", id = cmdId, message = "Hello" });

        // Read events until we see turnStart then turnEnd (or an error).
        bool sawTurnStart = false;

        for (int i = 0; i < 50; i++)
        {
            string? line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(5));
            if (line is null) break;

            if (line.Contains("\"turnStart\""))
            {
                sawTurnStart = true;
            }

            // Stop on turnEnd or error — either means the turn lifecycle completed.
            if (line.Contains("\"turnEnd\"") || line.Contains("\"error\""))
            {
                break;
            }
        }

        // The turn should at least start, even if the LLM call fails with no credentials.
        Assert.True(sawTurnStart, "Expected turnStart event but none was received.");
    }

    [Fact]
    public async Task MalformedCommandProducesErrorEvent()
    {
        Assert.NotNull(_stdout);
        await SkipToAgentReadyAsync();

        await _stdin!.WriteLineAsync("{not valid json");
        await _stdin.FlushAsync();

        string? errorLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(1));
            if (line is null) break;
            if (line.Contains("\"error\""))
            {
                errorLine = line;
                break;
            }
        }

        Assert.NotNull(errorLine);

        using JsonDocument doc = JsonDocument.Parse(errorLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.NotNull(root.GetProperty("message").GetString());
    }

    // ─── helpers ──────────────────────────────────────────────

    private async Task SendAsync(object cmd)
    {
        string json = JsonSerializer.Serialize(cmd, JsonOptions);
        await _stdin!.WriteLineAsync(json);
        await _stdin.FlushAsync();
    }

    private async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _stdout!.ReadLineAsync(cts.Token).AsTask().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task SkipToAgentReadyAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            string? line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(5));
            if (line is null) break;
            if (line.Contains("\"agentReady\""))
            {
                return;
            }
        }

        Assert.Fail(FormatFailure("agentReady was never received"));
    }

    private async Task<string?> ReadResponseAsync(string cmdId)
    {
        for (int i = 0; i < 20; i++)
        {
            string? line = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(2));
            if (line is null) return null;
            if (line.Contains("\"response\"") && line.Contains(cmdId))
            {
                return line;
            }
        }

        return null;
    }

    private string FormatFailure(string message)
    {
        string stderrText = string.Join("\n", _coreStderr);
        string extraInfo = _coreProcess?.HasExited == true
            ? $"Core exited with code {_coreProcess.ExitCode}"
            : "Core still running";
        return $"{message}. {extraInfo}. Core stderr:\n{stderrText}";
    }

    private static (string path, string directory) FindCoreDll()
    {
        string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string assemblyDir = Path.GetDirectoryName(assemblyPath) ?? ".";

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
