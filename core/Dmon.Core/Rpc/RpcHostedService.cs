using System.Reflection;
using System.Runtime.CompilerServices;
using Dmon.Core.Bootstrap;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public sealed class RpcHostedService : BackgroundService
{
    private readonly CommandDispatcher _dispatcher;
    private readonly IEventEmitter _emitter;
    private readonly BootstrapService _bootstrap;
    private readonly SetupCheckService _setupCheck;
    private readonly TextReader _stdin;
    private readonly ILogger<RpcHostedService> _logger;

    public RpcHostedService(
        CommandDispatcher dispatcher,
        IEventEmitter emitter,
        BootstrapService bootstrap,
        SetupCheckService setupCheck,
        TextReader stdin,
        ILogger<RpcHostedService> logger)
    {
        _dispatcher = dispatcher;
        _emitter = emitter;
        _bootstrap = bootstrap;
        _setupCheck = setupCheck;
        _stdin = stdin;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _bootstrap.RunAsync(stoppingToken).ConfigureAwait(false);
        await _setupCheck.RunAsync(stoppingToken).ConfigureAwait(false);

        string coreVersion = ResolveCoreVersion(Assembly.GetExecutingAssembly());

        await _emitter.EmitAsync(new AgentReadyEvent
        {
            ProtocolVersion = Dmon.Protocol.ProtocolVersion.Current,
            CoreVersion = coreVersion
        }, stoppingToken).ConfigureAwait(false);

        _logger.LogDebug("RPC loop started. Protocol {Protocol}, core {Version}.",
            Dmon.Protocol.ProtocolVersion.Current, coreVersion);

        await foreach (string line in ReadLinesAsync(_stdin, stoppingToken).ConfigureAwait(false))
        {
            await _dispatcher.DispatchAsync(line, stoppingToken).ConfigureAwait(false);
        }

        // Allow outstanding background tasks (turn.submit, wizard.start) to complete
        // before the hosted service tears down.
        await _dispatcher.DrainAsync().ConfigureAwait(false);
    }

    internal static string ResolveCoreVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    // Yields trimmed, non-blank lines from input. Ends on stdin EOF (null) or cancellation.
    // Single-reader: one line is fully dispatched before the next is read (no concurrency added).
    private static async IAsyncEnumerable<string> ReadLinesAsync(
        TextReader input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (line is null)
            {
                // stdin closed — host disconnected.
                yield break;
            }

            // Strip trailing CR for environments that send CRLF.
            line = line.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
        }
    }
}
