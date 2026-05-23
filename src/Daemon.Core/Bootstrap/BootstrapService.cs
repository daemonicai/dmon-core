using Daemon.Core.Rpc;
using Daemon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Daemon.Core.Bootstrap;

public sealed class BootstrapService
{
    private const string DaemonDir = ".daemon";
    private const string ConfigFileName = "config.yaml";
    private const string SessionsSubdir = "sessions";

    private static readonly string DefaultConfig =
        "# daemon configuration\n" +
        "# provider: anthropic | openai | gemini\n" +
        "provider: anthropic\n" +
        "sessionStore: local\n";

    private readonly IEventEmitter _emitter;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(IEventEmitter emitter, ILogger<BootstrapService> logger)
    {
        _emitter = emitter;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string cwd = Directory.GetCurrentDirectory();
        string daemonPath = Path.Combine(cwd, DaemonDir);

        if (DaemonRootExists(cwd))
        {
            return;
        }

        List<string> created = [];

        Directory.CreateDirectory(daemonPath);
        created.Add(daemonPath);

        string configPath = Path.Combine(daemonPath, ConfigFileName);
        await File.WriteAllTextAsync(configPath, DefaultConfig, cancellationToken).ConfigureAwait(false);
        created.Add(configPath);

        string sessionsPath = Path.Combine(daemonPath, SessionsSubdir);
        Directory.CreateDirectory(sessionsPath);
        created.Add(sessionsPath);

        _logger.LogDebug("Bootstrapped .daemon directory at {Path}.", daemonPath);

        await _emitter.EmitAsync(new BootstrapNoticeEvent
        {
            Path = daemonPath,
            Created = created
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool DaemonRootExists(string start)
    {
        string? current = Path.GetFullPath(start);

        while (current is not null)
        {
            string candidate = Path.Combine(current, DaemonDir, ConfigFileName);
            if (File.Exists(candidate))
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
