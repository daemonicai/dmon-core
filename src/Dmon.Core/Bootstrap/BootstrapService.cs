using Dmon.Core.Rpc;
using Dmon.Protocol.Events;

namespace Dmon.Core.Bootstrap;

public sealed class BootstrapService
{
    private const string DmonDir = ".dmon";
    private const string ConfigFileName = "config.yaml";
    private const string SessionsSubdir = "sessions";

    private static readonly string DefaultConfig =
        "# dmon configuration\n" +
        "# See docs/configuration.md for full reference.\n" +
        "\n" +
        "# Where session data is stored: local (project .dmon/), global (~/.dmon/), or an absolute path.\n" +
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
        "\n" +
        "# Override default settings (YAML dot-notation keys become IConfiguration paths).\n" +
        "# Dmon:Session:AttachmentThresholdBytes: 1024\n" +
        "# Dmon:Session:Compaction:Threshold: 100\n" +
        "# Dmon:Provider:Retry:BaseDelayMs: 1000\n" +
        "# Dmon:Provider:Retry:MaxDelayMs: 30000\n" +
        "# Dmon:Provider:Retry:MaxAttempts: 5\n" +
        "\n" +
        "# Extensions to load at startup. Each entry requires a 'source' identifying\n" +
        "# a NuGet package id (nuget:<id>), an assembly path, or a .csx script path.\n" +
        "# extensions:\n" +
        "#   - source: \"nuget:Acme.Tools\"\n";

    private readonly IEventEmitter _emitter;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(IEventEmitter emitter, ILogger<BootstrapService> logger)
    {
        _emitter = emitter;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string globalHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dmonPath = Path.Combine(globalHome, DmonDir);

        if (DmonRootExists())
        {
            return;
        }

        List<string> created = [];

        Directory.CreateDirectory(dmonPath);
        created.Add(dmonPath);

        string configPath = Path.Combine(dmonPath, ConfigFileName);
        await File.WriteAllTextAsync(configPath, DefaultConfig, cancellationToken).ConfigureAwait(false);
        created.Add(configPath);

        string sessionsPath = Path.Combine(dmonPath, SessionsSubdir);
        Directory.CreateDirectory(sessionsPath);
        created.Add(sessionsPath);

        _logger.LogDebug("Bootstrapped .dmon directory at {Path}.", dmonPath);

        await _emitter.EmitAsync(new BootstrapNoticeEvent
        {
            Path = dmonPath,
            Created = created
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool DmonRootExists()
    {
        // Global config counts as bootstrapped.
        string globalHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string globalCandidate = Path.Combine(globalHome, DmonDir, ConfigFileName);
        if (File.Exists(globalCandidate))
        {
            return true;
        }

        // Walk up from CWD looking for a project-local config.
        string? current = Path.GetFullPath(Directory.GetCurrentDirectory());

        while (current is not null)
        {
            string candidate = Path.Combine(current, DmonDir, ConfigFileName);
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
