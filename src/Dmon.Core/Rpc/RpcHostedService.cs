using System.Reflection;
using Dmon.Core.Bootstrap;
using Dmon.Core.Extensions;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class RpcHostedService : BackgroundService
{
    private readonly CommandDispatcher _dispatcher;
    private readonly IEventEmitter _emitter;
    private readonly BootstrapService _bootstrap;
    private readonly SetupCheckService _setupCheck;
    private readonly StartupExtensionLoader _startupExtensions;
    private readonly ILogger<RpcHostedService> _logger;

    public RpcHostedService(
        CommandDispatcher dispatcher,
        IEventEmitter emitter,
        BootstrapService bootstrap,
        SetupCheckService setupCheck,
        StartupExtensionLoader startupExtensions,
        ILogger<RpcHostedService> logger)
    {
        _dispatcher = dispatcher;
        _emitter = emitter;
        _bootstrap = bootstrap;
        _setupCheck = setupCheck;
        _startupExtensions = startupExtensions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _bootstrap.RunAsync(stoppingToken).ConfigureAwait(false);
        await _setupCheck.RunAsync(stoppingToken).ConfigureAwait(false);

        // Load config-declared extensions before announcing ready.
        // BuiltinToolsInitializer runs as IHostedService before ExecuteAsync,
        // so builtin tools are already registered at this point.
        await _startupExtensions.RunAsync(stoppingToken).ConfigureAwait(false);

        string coreVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        await _emitter.EmitAsync(new AgentReadyEvent
        {
            ProtocolVersion = "1.0",
            CoreVersion = coreVersion
        }, stoppingToken).ConfigureAwait(false);

        _logger.LogDebug("RPC loop started. Protocol 1.0, core {Version}.", coreVersion);

        while (!stoppingToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                // stdin closed — host disconnected.
                _logger.LogDebug("stdin closed; shutting down.");
                break;
            }

            // Strip trailing CR for environments that send CRLF.
            line = line.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await _dispatcher.DispatchAsync(line, stoppingToken).ConfigureAwait(false);
        }

        // Allow outstanding background tasks (turn.submit, wizard.start) to complete
        // before the hosted service tears down.
        await _dispatcher.DrainAsync().ConfigureAwait(false);
    }
}
