using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Providers.LlamaCpp;

public sealed class LlamaCppProviderExtension : IProviderExtension, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan IsRunningTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly LlamaCppOptions _options;
    private readonly LlamaCppRuntimeState _runtimeState;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<CancellationToken, Task<bool>>? _isRunningProbe;
    private readonly Func<int, string, CancellationToken, Task>? _startServerDelegate;
    private readonly Action<string>? _onWarning;
    private readonly Action<string>? _onServerLog;
    private readonly Func<string, string, IChatClient>? _probeClientFactory;

    private Process? _serverProcess;
    private bool _disposed;

    // Allows tests to inject a real dummy process so the dispose-kills-server assertion is meaningful.
    internal void SetServerProcess(Process process) => _serverProcess = process;

    // Exposes parsed options for test assertions without widening production visibility.
    internal LlamaCppOptions Options => _options;

    public string ProviderName => "llama.cpp";

    // Public constructor — used in production.
    public LlamaCppProviderExtension(
        LlamaCppOptions options,
        Action<string>? onWarning = null,
        Action<string>? onServerLog = null)
    {
        _options = options;
        _runtimeState = new LlamaCppRuntimeState();
        _onWarning = onWarning;
        _onServerLog = onServerLog;
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
    }

    // Internal constructor for testability — injected HttpClient (for IsRunningAsync + ListModelsAsync tests).
    internal LlamaCppProviderExtension(
        LlamaCppOptions options,
        LlamaCppRuntimeState runtimeState,
        HttpClient httpClient)
    {
        _options = options;
        _runtimeState = runtimeState;
        _httpClient = httpClient;
        _ownsHttpClient = false;
    }

    // Internal constructor for testability — injected probe + start delegate (for EnsureRunningAsync tests).
    internal LlamaCppProviderExtension(
        LlamaCppOptions options,
        LlamaCppRuntimeState runtimeState,
        Func<CancellationToken, Task<bool>> isRunningProbe,
        Func<int, string, CancellationToken, Task>? startServerDelegate = null,
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

    public bool IsApplicable()
    {
        string? resolved = ResolveServerPath();
        if (resolved is not null)
            return true;

        string remediation = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "brew install llama.cpp"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "install via winget (`winget search llama.cpp`) or from https://github.com/ggml-org/llama.cpp/releases"
                : "download from https://github.com/ggml-org/llama.cpp/releases";

        _onWarning?.Invoke(
            $"llama-server not found. Install it with: {remediation}. " +
            "Alternatively set LLAMA_SERVER_PATH to its absolute path.");

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
        if (string.IsNullOrWhiteSpace(_options.ModelId))
            throw new InvalidOperationException(
                "LlamaCpp ModelId is required. Set the ModelId option or the LLAMA_MODEL_ID environment variable.");

        // N1: Seed port + BaseUrl from options BEFORE the liveness check so an already-running
        // server on the configured port is detected and reused.
        if (_options.Port.HasValue && _runtimeState.Port == 0)
        {
            _runtimeState.Port = _options.Port.Value;
            _runtimeState.BaseUrl = $"http://{_options.Host}:{_options.Port.Value}/v1";
        }

        bool running = await (_isRunningProbe is not null
            ? _isRunningProbe(cancellationToken)
            : IsRunningAsync(cancellationToken)).ConfigureAwait(false);

        if (!running)
        {
            int port = SelectPort();
            string host = _options.Host;
            _runtimeState.Port = port;
            _runtimeState.BaseUrl = $"http://{host}:{port}/v1";

            // B1: Any exception (OperationCanceledException, TimeoutException, or other) during
            // spawn, readiness polling, or the tool-calling probe must kill the server process
            // before rethrowing. The only path on which the process survives this method is a
            // clean return. The probe is inside this region so cancellation propagates correctly
            // and never orphans a running llama-server.
            try
            {
                if (_startServerDelegate is not null)
                    await _startServerDelegate(port, host, cancellationToken).ConfigureAwait(false);
                else
                    SpawnServer(port, host);

                await PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);

                // B2/4.1-4.2: Tool-calling probe — non-fatal for model/template failures;
                // sets ToolCallingVerified before first CreateAsync. Cancellation propagates
                // so the outer catch can kill the server and rethrow.
                await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                KillServer();
                throw;
            }
        }
        else
        {
            // B2/4.1-4.2: Server was already running — still run the probe so ToolCallingVerified
            // is set before the first CreateAsync call.
            await RunToolCallingProbeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string url = string.IsNullOrEmpty(_runtimeState.BaseUrl)
                ? $"http://{_options.Host}:{_runtimeState.Port}/v1/models"
                : $"{_runtimeState.BaseUrl}/models";

            HttpResponseMessage response = await _httpClient
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return [];

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LlamaCppModelsResponse? result = JsonSerializer.Deserialize(
                body, LlamaCppJsonContext.Default.LlamaCppModelsResponse);

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

    public IProviderFactory CreateFactory() => new LlamaCppProviderFactory(_options, _runtimeState);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

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
        string? envPath = Environment.GetEnvironmentVariable("LLAMA_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        // 3. PATH lookup
        return FindOnPath("llama-server");
    }

    private static string? FindOnPath(string executable)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;

        char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? [".exe", ".cmd", ".bat"]
            : [""];

        foreach (string dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dir, executable + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private int SelectPort()
    {
        if (_options.Port.HasValue)
            return _options.Port.Value;

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Extracted so tests can assert the argument list without spawning a real process.
    internal IReadOnlyList<string> BuildServerArguments(int port, string host)
    {
        string repoArg = _options.ModelId.Contains(':')
            ? _options.ModelId
            : $"{_options.ModelId}:{_options.Quant}";

        List<string> args =
        [
            "--jinja",
            "-hf",
            repoArg,
            "--port",
            port.ToString(),
            "--host",
            host,
        ];

        if (_options.ContextSize.HasValue)
        {
            args.Add("-c");
            args.Add(_options.ContextSize.Value.ToString());
        }

        if (_options.GpuLayers.HasValue)
        {
            args.Add("-ngl");
            args.Add(_options.GpuLayers.Value.ToString());
        }

        foreach (string arg in _options.ExtraArgs)
            args.Add(arg);

        return args;
    }

    private void SpawnServer(int port, string host)
    {
        string serverPath = ResolveServerPath()
            ?? throw new InvalidOperationException(
                "llama-server executable not found. Verify IsApplicable() returned true before calling EnsureRunningAsync.");

        ProcessStartInfo psi = new(serverPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };

        foreach (string arg in BuildServerArguments(port, host))
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
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            bool ready = await (_isRunningProbe is not null
                ? _isRunningProbe(cancellationToken)
                : IsRunningAsync(cancellationToken)).ConfigureAwait(false);

            if (ready)
                return;
        }

        KillServer();
        throw new TimeoutException(
            $"llama-server did not become ready within {timeout.TotalSeconds} seconds.");
    }

    private async Task RunToolCallingProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            IChatClient client = _probeClientFactory is not null
                ? _probeClientFactory(_options.ModelId, _runtimeState.BaseUrl)
                : new ChatClient(
                    _options.ModelId,
                    new ApiKeyCredential("none"),
                    new OpenAIClientOptions { Endpoint = new Uri(_runtimeState.BaseUrl) })
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
                    "llama-server tool-calling probe succeeded but returned no tool call — " +
                    "tools disabled for this session. Ensure the model's chat template supports tools " +
                    "and the server was launched with --jinja.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _runtimeState.ToolCallingVerified = false;
            _onWarning?.Invoke(
                $"llama-server tool-calling probe failed — tools disabled for this session. " +
                $"Ensure the model's chat template supports tools and the server was launched with --jinja. " +
                $"({ex.GetType().Name}: {ex.Message})");
        }
    }

    private async Task<bool> CheckRunningViaHttpAsync(CancellationToken cancellationToken)
    {
        try
        {
            string host = _options.Host;
            int port = _runtimeState.Port;

            if (port == 0)
                return false;

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(IsRunningTimeout);

            // Step 1: /health must return 200 (503 = still loading, not ready)
            HttpResponseMessage healthResponse = await _httpClient
                .GetAsync($"http://{host}:{port}/health", cts.Token)
                .ConfigureAwait(false);

            if (healthResponse.StatusCode != HttpStatusCode.OK)
                return false;

            // Step 2: verify identity via /v1/models (port-openness alone is insufficient)
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

internal sealed class LlamaCppModelsResponse
{
    [JsonPropertyName("data")]
    public List<LlamaCppModelEntry>? Data { get; set; }
}

internal sealed class LlamaCppModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

[JsonSerializable(typeof(LlamaCppModelsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LlamaCppJsonContext : JsonSerializerContext
{
}
