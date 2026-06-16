using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Providers.Mtplx;

public sealed class MtplxProviderExtension : IProviderExtension, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan IsRunningTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly MtplxOptions _options;
    private readonly MtplxRuntimeState _runtimeState;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<CancellationToken, Task<bool>>? _isRunningProbe;
    private readonly Func<int, CancellationToken, Task>? _startServerDelegate;
    private readonly Action<string>? _onWarning;
    private readonly Action<string>? _onServerLog;
    private readonly Func<string, string, IChatClient>? _probeClientFactory;
    private readonly Func<bool>? _isMacOsOverride;
    private readonly Func<Architecture>? _osArchitectureOverride;
    private readonly Func<string?>? _resolveServerPathOverride;

    private Process? _serverProcess;
    private bool _disposed;

    // Allows tests to inject a real dummy process so the dispose-kills-server assertion is meaningful.
    internal void SetServerProcess(Process process) => _serverProcess = process;

    // Exposes parsed options for test assertions without widening production visibility.
    internal MtplxOptions Options => _options;

    public string ProviderName => "mtplx";

    // Public constructor — used in production.
    public MtplxProviderExtension(
        MtplxOptions options,
        Action<string>? onWarning = null,
        Action<string>? onServerLog = null)
    {
        _options = options;
        _runtimeState = new MtplxRuntimeState();
        _onWarning = onWarning;
        _onServerLog = onServerLog;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — injected HttpClient (for IsRunningAsync + ListModelsAsync tests).
    internal MtplxProviderExtension(
        MtplxOptions options,
        MtplxRuntimeState runtimeState,
        HttpClient httpClient)
    {
        _options = options;
        _runtimeState = runtimeState;
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    // Internal constructor for testability — injected probe + start delegate (for EnsureRunningAsync tests).
    internal MtplxProviderExtension(
        MtplxOptions options,
        MtplxRuntimeState runtimeState,
        Func<CancellationToken, Task<bool>> isRunningProbe,
        Func<int, CancellationToken, Task>? startServerDelegate = null,
        Func<string, string, IChatClient>? probeClientFactory = null,
        Action<string>? onWarning = null)
    {
        _options = options;
        _runtimeState = runtimeState;
        _isRunningProbe = isRunningProbe;
        _startServerDelegate = startServerDelegate;
        _probeClientFactory = probeClientFactory;
        _onWarning = onWarning;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — injected OS/arch/resolve overrides (for IsApplicable tests).
    internal MtplxProviderExtension(
        MtplxOptions options,
        Func<bool> isMacOsOverride,
        Func<Architecture> osArchitectureOverride,
        Func<string?> resolveServerPathOverride,
        Action<string>? onWarning = null)
    {
        _options = options;
        _runtimeState = new MtplxRuntimeState();
        _isMacOsOverride = isMacOsOverride;
        _osArchitectureOverride = osArchitectureOverride;
        _resolveServerPathOverride = resolveServerPathOverride;
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
                "MTPLX requires macOS on Apple Silicon. This system is not running macOS.");
            return false;
        }

        Architecture arch = (_osArchitectureOverride ?? (() => RuntimeInformation.OSArchitecture))();
        if (arch != Architecture.Arm64)
        {
            _onWarning?.Invoke(
                "MTPLX requires Apple Silicon (arm64). This Mac is running on a non-arm64 architecture.");
            return false;
        }

        string? resolved = (_resolveServerPathOverride ?? ResolveServerPath)();
        if (resolved is not null)
            return true;

        _onWarning?.Invoke(
            "mtplx binary not found. Install it with: brew install youssofal/mtplx/mtplx. " +
            "Alternatively set MTPLX_SERVER_PATH to its absolute path.");
        return false;
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunningProbe is not null)
            return await _isRunningProbe(cancellationToken).ConfigureAwait(false);

        return await CheckRunningViaHttpAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        // Seed BaseUrl from configured host/port before the liveness check so an already-running
        // server on the configured port is detected and reused.
        string baseUrl = $"http://{_options.Host}:{_options.Port}/v1";
        _runtimeState.BaseUrl = baseUrl;

        bool running = await IsRunningAsync(cancellationToken).ConfigureAwait(false);

        if (running)
        {
            // Attach-first: server already answers — record that we do not own the process.
            _runtimeState.OwnsProcess = false;

            // Ensure the target model is known before the tool-calling probe.
            await EnsureActiveModelSeededAsync(cancellationToken).ConfigureAwait(false);

            // B2: Tool-calling probe — non-fatal; sets ToolCallingVerified before first CreateAsync.
            await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Nothing listening: start `mtplx serve --port <port>`.
        _runtimeState.OwnsProcess = true;

        // B1: Any exception during spawn, readiness polling, or the tool-calling probe must kill
        // the server process before rethrowing. The only path on which the process survives this
        // method is a clean return. The probe is inside this region so cancellation propagates
        // correctly and never orphans a running mtplx process.
        try
        {
            if (_startServerDelegate is not null)
                await _startServerDelegate(_options.Port, cancellationToken).ConfigureAwait(false);
            else
                SpawnServer(_options.Port);

            await PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);

            // Ensure the target model is known before the tool-calling probe.
            await EnsureActiveModelSeededAsync(cancellationToken).ConfigureAwait(false);

            // B2: Tool-calling probe — non-fatal; sets ToolCallingVerified before first CreateAsync.
            await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            KillServer();
            throw;
        }
    }

    // Seeds ActiveModelId from /v1/models when ModelId is not explicitly configured.
    // ListModelsAsync is safe to call here — it catches all errors and returns [].
    private async Task EnsureActiveModelSeededAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.ModelId) && string.IsNullOrEmpty(_runtimeState.ActiveModelId))
            await ListModelsAsync(cancellationToken).ConfigureAwait(false);
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
            MtplxModelsResponse? result = JsonSerializer.Deserialize(
                body, MtplxJsonContext.Default.MtplxModelsResponse);

            if (result?.Data is null)
                return [];

            string? activeModelId = result.Data.FirstOrDefault()?.Id;

            // When ModelId is unset, target the server's reported active model.
            if (string.IsNullOrEmpty(_options.ModelId) && activeModelId is not null)
                _runtimeState.ActiveModelId = activeModelId;

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

    public IProviderFactory CreateFactory() => new MtplxProviderFactory(_options, _runtimeState);

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

    private string? ResolveServerPath()
    {
        // 1. Explicit option
        if (!string.IsNullOrWhiteSpace(_options.ServerPath) && File.Exists(_options.ServerPath))
            return _options.ServerPath;

        // 2. Environment variable
        string? envPath = Environment.GetEnvironmentVariable("MTPLX_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        // 3. PATH lookup
        return FindOnPath("mtplx");
    }

    private static string? FindOnPath(string executable)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;

        // macOS/arm64-only provider; Unix PATH separator and no extension probing.
        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private void SpawnServer(int port)
    {
        string serverPath = ResolveServerPath()
            ?? throw new InvalidOperationException(
                "mtplx executable not found. Verify IsApplicable() returned true before calling EnsureRunningAsync.");

        // `mtplx serve --port <port>` — the model is already loaded by the runtime;
        // no model path argument is needed for the serve sub-command.
        ProcessStartInfo psi = new(serverPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());

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
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            bool ready = await IsRunningAsync(cancellationToken).ConfigureAwait(false);

            if (ready)
                return;
        }

        KillServer();
        throw new TimeoutException(
            $"mtplx did not become ready within {timeout.TotalSeconds} seconds.");
    }

    private async Task RunToolCallingProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string modelId = _options.ModelId ?? _runtimeState.ActiveModelId ?? string.Empty;
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

            ChatOptions chatOptions = new() { Tools = [probe] };

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
                    "mtplx tool-calling probe succeeded but returned no tool call — " +
                    "tools disabled for this session. Ensure the loaded model supports function calling.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _runtimeState.ToolCallingVerified = false;
            _onWarning?.Invoke(
                $"mtplx tool-calling probe failed — tools disabled for this session. " +
                $"Ensure the loaded model supports function calling. " +
                $"({ex.GetType().Name}: {ex.Message})");
        }
    }

    private async Task<bool> CheckRunningViaHttpAsync(CancellationToken cancellationToken)
    {
        try
        {
            string host = _options.Host;
            int port = _options.Port;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(IsRunningTimeout);

            // Step 1: /health must return 200 (not just a TCP connection)
            HttpResponseMessage healthResponse = await _httpClient
                .GetAsync($"http://{host}:{port}/health", cts.Token)
                .ConfigureAwait(false);

            if (healthResponse.StatusCode != HttpStatusCode.OK)
                return false;

            // Step 2: verify server identity via /v1/models (port-openness alone is insufficient)
            HttpResponseMessage modelsResponse = await _httpClient
                .GetAsync($"http://{host}:{port}/v1/models", cts.Token)
                .ConfigureAwait(false);

            return modelsResponse.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
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
}

// JSON model types for /v1/models response

internal sealed class MtplxModelsResponse
{
    [JsonPropertyName("data")]
    public List<MtplxModelEntry>? Data { get; set; }
}

internal sealed class MtplxModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

[JsonSerializable(typeof(MtplxModelsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class MtplxJsonContext : JsonSerializerContext
{
}
