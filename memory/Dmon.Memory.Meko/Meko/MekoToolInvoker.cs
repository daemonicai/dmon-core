using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Real <see cref="IMekoToolInvoker"/> over a live <see cref="McpClient"/>.
/// Connects lazily on first call — <see cref="McpClient.CreateAsync"/> is NOT
/// called at construction or DI-registration time (D12).
/// Thread-safe: a single <see cref="SemaphoreSlim"/> serializes lazy init.
/// </summary>
internal sealed class MekoToolInvoker : IMekoToolInvoker, IAsyncDisposable
{
    private readonly MekoLongTermOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    public MekoToolInvoker(MekoLongTermOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public async Task<CallToolResult> CallToolAsync(
        string tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        McpClient client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallToolAsync(tool, args, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = _options.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    // Key never written to logs; only the header name appears here.
                    ["Authorization"] = $"Bearer {_options.ApiKey}",
                },
            };

            var transport = new HttpClientTransport(transportOptions, _loggerFactory);
            _client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initLock.Dispose();

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
