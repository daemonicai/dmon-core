using Dmon.Core.Config;
using Dmon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Extensions;

/// <summary>
/// Loads config-declared extensions at daemon startup without prompting.
/// Config presence is the prior approval (design D5); no ConfirmCallback is installed.
/// Failures are logged per-entry and do not abort startup.
/// </summary>
public sealed class StartupExtensionLoader
{
    private readonly ExtensionService _extensionService;
    private readonly EffectiveExtensionSetResolver _resolver;
    private readonly ILogger<StartupExtensionLoader> _logger;

    public StartupExtensionLoader(
        ExtensionService extensionService,
        EffectiveExtensionSetResolver resolver,
        ILogger<StartupExtensionLoader> logger)
    {
        _extensionService = extensionService;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the effective extension set from the standard user and project config
    /// paths, then loads each entry. Mirrors the path logic in BootstrapService and Program.cs.
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        string userConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon", "config.yaml");

        string projectConfig = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".dmon", "config.yaml");

        IReadOnlyList<ExtensionEntry> entries = _resolver.Resolve(userConfig, projectConfig);
        return LoadEntriesAsync(entries, cancellationToken);
    }

    /// <summary>
    /// Loads each entry in <paramref name="entries"/> via <see cref="ExtensionService"/>,
    /// logging per-entry outcomes. No exception escapes — a failed entry is skipped.
    /// Separated from <see cref="RunAsync"/> so tests can drive it without file I/O.
    /// </summary>
    public async Task LoadEntriesAsync(
        IReadOnlyList<ExtensionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        int loaded = 0;
        int failed = 0;

        // Subscribe to Error for the duration so we can log failures that surface as events.
        // ExtensionService.LoadAsync routes most failures through Error rather than throwing,
        // but the provider registration path (outside its inner try/catch) can throw directly.
        void OnError(ExtensionErrorEvent e)
        {
            _logger.LogWarning(
                "Startup extension '{Source}' failed at phase '{Phase}': {Diagnostics}",
                e.Source,
                e.Phase,
                string.Join("; ", e.Diagnostics));
            failed++;
        }

        void OnLoaded(ExtensionLoadedEvent e)
        {
            _logger.LogInformation(
                "Startup extension '{Name}' loaded ({ToolCount} tool(s)).",
                e.Name,
                e.Tools.Count);
            loaded++;
        }

        _extensionService.Error += OnError;
        _extensionService.Loaded += OnLoaded;

        try
        {
            foreach (ExtensionEntry entry in entries)
            {
                try
                {
                    await _extensionService.LoadAsync(entry.Source, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Guards against provider registration path or any other uncaught throw.
                    _logger.LogWarning(
                        ex,
                        "Startup extension '{Source}' threw unexpectedly; skipping.",
                        entry.Source);
                    failed++;
                }
            }
        }
        finally
        {
            _extensionService.Error -= OnError;
            _extensionService.Loaded -= OnLoaded;
        }

        _logger.LogInformation(
            "Startup extensions: {Loaded} loaded, {Failed} failed.",
            loaded,
            failed);
    }
}
