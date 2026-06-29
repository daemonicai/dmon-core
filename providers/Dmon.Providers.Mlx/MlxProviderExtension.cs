using System.ClientModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Providers.Mlx;

public sealed class MlxProviderExtension : IProviderExtension, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan IsRunningTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly Version MlxLmVersionPin = new(0, 31, 3);

    // Shared venv path across both runtimes — provisioning is idempotent.
    private static readonly string VenvPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dmon", "mlx", "venv");

    private readonly MlxRuntimeOptions _options;
    private readonly MlxRuntimeState _runtimeState;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<bool>? _isMacOsOverride;
    private readonly Func<Architecture>? _osArchitectureOverride;
    private readonly Func<string?>? _resolveUvPathOverride;
    private readonly Func<CancellationToken, Task<bool>>? _isRunningProbe;
    private readonly Func<CancellationToken, Task<string>>? _provisionEnvDelegate;
    private readonly Func<int, CancellationToken, Task>? _startServerDelegate;
    private readonly Func<string, string, IChatClient>? _probeClientFactory;
    private readonly Action<string>? _onWarning;
    private readonly Action<string>? _onServerLog;

    private Process? _serverProcess;
    private bool _disposed;

    // Allows tests to inject a real dummy process so dispose-kills-server assertions are meaningful.
    internal void SetServerProcess(Process process) => _serverProcess = process;

    // Exposes parsed options for test assertions without widening production visibility.
    internal MlxRuntimeOptions Options => _options;

    public string ProviderName => "mlx";

    // Public constructor — used in production.
    public MlxProviderExtension(
        MlxRuntimeOptions options,
        Action<string>? onWarning = null,
        Action<string>? onServerLog = null)
    {
        _options = options;
        _runtimeState = new MlxRuntimeState();
        _onWarning = onWarning;
        _onServerLog = onServerLog;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — injected OS/arch/uv-resolve overrides (for IsApplicable tests).
    internal MlxProviderExtension(
        MlxRuntimeOptions options,
        Func<bool> isMacOsOverride,
        Func<Architecture> osArchitectureOverride,
        Func<string?> resolveUvPathOverride,
        Action<string>? onWarning = null)
    {
        _options = options;
        _runtimeState = new MlxRuntimeState();
        _isMacOsOverride = isMacOsOverride;
        _osArchitectureOverride = osArchitectureOverride;
        _resolveUvPathOverride = resolveUvPathOverride;
        _onWarning = onWarning;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — injected HttpClient (for ListModelsAsync tests).
    internal MlxProviderExtension(
        MlxRuntimeOptions options,
        MlxRuntimeState runtimeState,
        HttpClient httpClient)
    {
        _options = options;
        _runtimeState = runtimeState;
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    // Internal constructor for testability — completion-based readiness path (no _isRunningProbe).
    // Use when asserting IsRunningAsync calls a completion and never /v1/models.
    internal MlxProviderExtension(
        MlxRuntimeOptions options,
        MlxRuntimeState runtimeState,
        Func<string, string, IChatClient> probeClientFactory,
        Action<string>? onWarning = null)
    {
        _options = options;
        _runtimeState = runtimeState;
        _probeClientFactory = probeClientFactory;
        _onWarning = onWarning;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — full EnsureRunningAsync lifecycle seam.
    internal MlxProviderExtension(
        MlxRuntimeOptions options,
        MlxRuntimeState runtimeState,
        Func<CancellationToken, Task<bool>> isRunningProbe,
        Func<CancellationToken, Task<string>> provisionEnvDelegate,
        Func<int, CancellationToken, Task>? startServerDelegate = null,
        Func<string, string, IChatClient>? probeClientFactory = null,
        Action<string>? onWarning = null)
    {
        _options = options;
        _runtimeState = runtimeState;
        _isRunningProbe = isRunningProbe;
        _provisionEnvDelegate = provisionEnvDelegate;
        _startServerDelegate = startServerDelegate;
        _probeClientFactory = probeClientFactory;
        _onWarning = onWarning;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    public bool IsApplicable()
    {
        bool isMacOs = (_isMacOsOverride ?? OperatingSystem.IsMacOS)();
        if (!isMacOs)
        {
            _onWarning?.Invoke(
                "MLX requires macOS on Apple Silicon. This system is not running macOS.");
            return false;
        }

        Architecture arch = (_osArchitectureOverride ?? (() => RuntimeInformation.OSArchitecture))();
        if (arch != Architecture.Arm64)
        {
            _onWarning?.Invoke(
                "MLX requires Apple Silicon (arm64). This Mac is running on a non-arm64 architecture.");
            return false;
        }

        string? uvPath = (_resolveUvPathOverride ?? FindUvOnPath)();
        if (uvPath is not null)
            return true;

        _onWarning?.Invoke(
            "uv not found on PATH. Install uv with: curl -LsSf https://astral.sh/uv/install.sh | sh. " +
            "uv is required to manage the mlx_lm Python environment.");
        return false;
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunningProbe is not null)
            return await _isRunningProbe(cancellationToken).ConfigureAwait(false);

        return await CheckRunningViaCompletionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelId))
            throw new InvalidOperationException(
                "MLX ModelId is required. Use MlxRuntimeOptions.Firstline() or .Escalation() to get validated defaults.");

        // Provision the uv-managed venv and resolve the installed mlx_lm version.
        string resolvedVersion = _provisionEnvDelegate is not null
            ? await _provisionEnvDelegate(cancellationToken).ConfigureAwait(false)
            : await ProvisionEnvAsync(cancellationToken).ConfigureAwait(false);

        // Fail fast if below the pin — versions < 0.31.3 silently drop tool calls
        // (missing gemma-4 parser, issue #1096).
        if (IsVersionBelowPin(resolvedVersion))
            throw new InvalidOperationException(
                $"Installed mlx_lm version '{resolvedVersion}' is below the required minimum " +
                $"{MlxLmVersionPin}. Upgrade: uv pip install 'mlx_lm>={MlxLmVersionPin}'. " +
                $"Version {MlxLmVersionPin} ships the gemma-4 tool parser; earlier versions drop tool calls silently.");

        // Seed BaseUrl before the liveness check so an already-running server is detected and reused.
        _runtimeState.BaseUrl = $"http://{_options.Host}:{_options.Port}/v1";

        bool running = await IsRunningAsync(cancellationToken).ConfigureAwait(false);

        if (running)
        {
            // Attach-first: server already answers — we do not own the process.
            _runtimeState.OwnsProcess = false;
            // B2: tool-calling probe — non-fatal; sets ToolCallingVerified before first CreateAsync.
            await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Nothing listening: spawn <venv>/bin/python -m mlx_lm.server --model ... --port ... --host ...
        _runtimeState.OwnsProcess = true;

        // B1: any exception during spawn, readiness polling, or the tool-calling probe kills the
        // server before rethrowing — no orphaned mlx_lm.server processes.
        try
        {
            if (_startServerDelegate is not null)
                await _startServerDelegate(_options.Port, cancellationToken).ConfigureAwait(false);
            else
                SpawnServer(_options.Port);

            await PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);

            // B2: tool-calling probe — non-fatal; sets ToolCallingVerified before first CreateAsync.
            await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            KillServer();
            throw;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string url = string.IsNullOrEmpty(_runtimeState.BaseUrl)
                ? $"http://{_options.Host}:{_options.Port}/v1/models"
                : $"{_runtimeState.BaseUrl}/models";

            HttpResponseMessage response = await _httpClient
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return [];

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            MlxModelsResponse? result = JsonSerializer.Deserialize(
                body, MlxJsonContext.Default.MlxModelsResponse);

            if (result?.Data is null)
                return [];

            return result.Data
                .Select(m => new ModelInfo
                {
                    Id = m.Id ?? string.Empty,
                    Capabilities = new ChatClientCapabilities
                    {
                        SupportsToolCalling = _runtimeState.ToolCallingVerified ?? false,
                    },
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public IProviderFactory CreateFactory() => new MlxProviderFactory(_options, _runtimeState);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Mirror the Dispose() ownership guard: only kill a process dmon started;
        // leave an attached server running.
        if (_runtimeState.OwnsProcess)
            KillServer();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Only kill a process dmon started; leave an attached server running.
        if (_runtimeState.OwnsProcess)
            KillServer();

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Private helpers ---

    private static bool IsVersionBelowPin(string versionString)
    {
        if (Version.TryParse(versionString.Trim(), out Version? version))
            return version < MlxLmVersionPin;

        // Unparseable version string — fail-fast to avoid silently running a broken env.
        throw new InvalidOperationException(
            $"mlx_lm resolved version '{versionString.Trim()}' could not be parsed as a version number.");
    }

    private async Task<string> ProvisionEnvAsync(CancellationToken cancellationToken)
    {
        string uvPath = FindUvOnPath()
            ?? throw new InvalidOperationException(
                "uv not found on PATH. IsApplicable() must return true before calling EnsureRunningAsync. " +
                "Install uv with: curl -LsSf https://astral.sh/uv/install.sh | sh.");

        // 1. Create venv (idempotent — uv venv is a no-op when it already exists).
        await RunSubprocessAsync(uvPath, ["venv", VenvPath], cancellationToken).ConfigureAwait(false);

        // 2. Install / upgrade mlx_lm to at least the pinned version.
        string venvPython = Path.Combine(VenvPath, "bin", "python");
        await RunSubprocessAsync(
            uvPath,
            ["pip", "install", "--python", venvPython, $"mlx_lm>={MlxLmVersionPin}"],
            cancellationToken).ConfigureAwait(false);

        // 3. Resolve the installed version.
        string output = await RunSubprocessOutputAsync(
            venvPython,
            ["-c", "import importlib.metadata; print(importlib.metadata.version('mlx-lm'))"],
            cancellationToken).ConfigureAwait(false);

        return output.Trim();
    }

    // Extracted so tests can assert the argument list without spawning a real process.
    internal IReadOnlyList<string> BuildServerArguments(int port) =>
        ["-m", "mlx_lm.server", "--model", _options.ModelId, "--port", port.ToString(), "--host", _options.Host];

    private void SpawnServer(int port)
    {
        string venvPython = Path.Combine(VenvPath, "bin", "python");
        IReadOnlyList<string> args = BuildServerArguments(port);

        ProcessStartInfo psi = new(venvPython)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        Process process = new() { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _onServerLog?.Invoke(e.Data);
        };

        process.Start();
        _serverProcess = process;
        process.BeginErrorReadLine();
    }

    private async Task PollUntilReadyAsync(CancellationToken cancellationToken)
    {
        TimeSpan timeout = _options.ReadyTimeout;
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // Check before delaying so an immediately-ready server returns without waiting a
            // full PollInterval, and a ReadyTimeout shorter than PollInterval still fires.
            bool ready = await IsRunningAsync(cancellationToken).ConfigureAwait(false);

            if (ready)
                return;

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        KillServer();
        throw new TimeoutException(
            $"mlx_lm.server did not become ready within {timeout.TotalSeconds} seconds.");
    }

    // Readiness check: confirm the resident model responds to a minimal completion.
    // /v1/models is NOT used here — it lists cached-not-resident models and cannot prove
    // the model currently in VRAM answers. A successful completion proves it does.
    // gemma-4 may emit an empty 'content' due to the separate 'reasoning' field; a successful
    // HTTP response (no exception) is sufficient — we do not require non-empty content.
    private async Task<bool> CheckRunningViaCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            string baseUrl = _runtimeState.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
                return false;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(IsRunningTimeout);

            string modelId = _options.ModelId;
            IChatClient client = _probeClientFactory is not null
                ? _probeClientFactory(modelId, baseUrl)
                : new ChatClient(
                    modelId,
                    new ApiKeyCredential("none"),
                    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
                    .AsIChatClient();

            // Small MaxOutputTokens: liveness only — we do not need content.
            ChatOptions opts = new() { MaxOutputTokens = 16 };
            await client.GetResponseAsync(".", opts, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunToolCallingProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string modelId = _options.ModelId;
            string baseUrl = _runtimeState.BaseUrl;

            IChatClient client = _probeClientFactory is not null
                ? _probeClientFactory(modelId, baseUrl)
                : new ChatClient(
                    modelId,
                    new ApiKeyCredential("none"),
                    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
                    .AsIChatClient();

            AIFunction probe = AIFunctionFactory.Create(
                () => 42,
                "get_test_value",
                "Returns a constant test value.");

            // Generous MaxOutputTokens: gemma-4 emits a separate 'reasoning' field that consumes
            // tokens before the tool call. A low limit causes false-negative probe results.
            ChatOptions chatOptions = new() { Tools = [probe], MaxOutputTokens = 2048 };

            ChatResponse response = await client
                .GetResponseAsync("Call the get_test_value tool.", chatOptions, cancellationToken)
                .ConfigureAwait(false);

            bool hasFunctionCall = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any();

            _runtimeState.ToolCallingVerified = hasFunctionCall;

            if (!hasFunctionCall)
                _onWarning?.Invoke(
                    "mlx_lm tool-calling probe succeeded but returned no tool call — " +
                    "tools disabled for this session. Ensure the loaded model supports function calling.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _runtimeState.ToolCallingVerified = false;
            _onWarning?.Invoke(
                $"mlx_lm tool-calling probe failed — tools disabled for this session. " +
                $"Ensure the loaded model supports function calling. " +
                $"({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void KillServer()
    {
        Process? process = Interlocked.Exchange(ref _serverProcess, null);
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort kill; process may have already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task RunSubprocessAsync(string executable, string[] args, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = psi };
        process.Start();
        // Drain both streams concurrently to avoid deadlocks on large output.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken))
            .ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Subprocess '{executable}' failed (exit {process.ExitCode}): {stderr.Result.Trim()}");
    }

    private static async Task<string> RunSubprocessOutputAsync(string executable, string[] args, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = psi };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken))
            .ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Subprocess '{executable}' failed (exit {process.ExitCode}): {stderr.Result.Trim()}");

        return stdout.Result;
    }

    private static string? FindUvOnPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;

        // macOS/arm64-only provider; Unix PATH separator, no extension probing needed.
        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "uv");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

// JSON model types for /v1/models response.

internal sealed class MlxModelsResponse
{
    [JsonPropertyName("data")]
    public List<MlxModelEntry>? Data { get; set; }
}

internal sealed class MlxModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

[JsonSerializable(typeof(MlxModelsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class MlxJsonContext : JsonSerializerContext
{
}
