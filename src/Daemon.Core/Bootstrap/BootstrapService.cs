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
        "# See docs/configuration.md for full reference.\n" +
        "\n" +
        "# Where session data is stored: local (project .daemon/), global (~/.daemon/), or an absolute path.\n" +
        "sessionStore: local\n" +
        "\n" +
        "# Provider definitions. Add one block per provider (anthropic, openai, gemini).\n" +
        "providers:\n" +
        "  # example:\n" +
        "  #   adapter: anthropic\n" +
        "  #   defaultModelId: claude-sonnet-4-20250514\n" +
        "  #   auth:\n" +
        "  #     type: envVar\n" +
        "  #     envVar: ANTHROPIC_API_KEY\n" +
        "  #   capabilities:\n" +
        "  #     toolCalling: true\n" +
        "  #     reasoning: true\n" +
        "  #     contextWindow: 200000\n" +
        "  #     maxTokens: 4096\n" +
        "\n" +
        "# Override default settings (YAML dot-notation keys become IConfiguration paths).\n" +
        "# Daemon:Session:AttachmentThresholdBytes: 1024\n" +
        "# Daemon:Session:Compaction:Threshold: 100\n" +
        "# Daemon:Provider:Retry:BaseDelayMs: 1000\n" +
        "# Daemon:Provider:Retry:MaxDelayMs: 30000\n" +
        "# Daemon:Provider:Retry:MaxAttempts: 5\n";

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
