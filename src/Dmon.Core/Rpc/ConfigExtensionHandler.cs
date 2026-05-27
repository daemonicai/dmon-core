using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Extensions.Security;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Rpc;

/// <summary>
/// Handles extension.load by writing the source to the chosen config scope's
/// extensions list and reporting "reload required". This handler deliberately has
/// NO dependency on ExtensionService or IToolRegistry — the structural guarantee
/// that it never loads anything into the running process.
/// </summary>
internal class ConfigExtensionHandler : IExtensionHandler
{
    private readonly IEventEmitter _emitter;
    private readonly IExtensionSourceFetcher _sourceFetcher;
    private readonly IExtensionSecurityAnalyser _securityAnalyser;
    private readonly ExtensionsConfigReader _configReader;
    private readonly ILogger<ConfigExtensionHandler> _logger;

    public ConfigExtensionHandler(
        IEventEmitter emitter,
        IExtensionSourceFetcher sourceFetcher,
        IExtensionSecurityAnalyser securityAnalyser,
        ExtensionsConfigReader configReader,
        ILogger<ConfigExtensionHandler> logger)
    {
        _emitter = emitter;
        _sourceFetcher = sourceFetcher;
        _securityAnalyser = securityAnalyser;
        _configReader = configReader;
        _logger = logger;
    }

    public async Task LoadAsync(ExtensionLoadCommand cmd, CancellationToken cancellationToken)
    {
        string trimmedSource = (cmd.Source ?? string.Empty).Trim();

        if (!IsWritableSource(trimmedSource))
        {
            await _emitter.EmitAsync(new ExtensionErrorEvent
            {
                Source = trimmedSource,
                Phase = "validation",
                Diagnostics = ["Invalid extension source: must not contain quotes, newlines, or control characters."]
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        string scope = string.IsNullOrWhiteSpace(cmd.Scope) ? "project" : cmd.Scope;
        string? configPath = ResolveConfigPath(scope);

        if (configPath is null)
        {
            await _emitter.EmitAsync(new ExtensionErrorEvent
            {
                Source = trimmedSource,
                Phase = "validation",
                Diagnostics = [$"Unknown scope '{scope}'. Expected 'project' or 'user'."]
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            ParsedExtensionSource parsed = ParsedExtensionSource.Parse(trimmedSource);

            // ADR-006 gate: only NuGet sources undergo source-fetch + security analysis.
            // Local script/assembly paths are user-controlled files — the fetch step is
            // NuGet-specific and does not apply (consistent with "manual config edit is always valid").
            string riskDisplay = "n/a";
            if (parsed.Kind == "nuget")
            {
                if (string.IsNullOrWhiteSpace(parsed.Version))
                {
                    await _emitter.EmitAsync(new ExtensionErrorEvent
                    {
                        Source = trimmedSource,
                        Phase = "validation",
                        Diagnostics = [$"NuGet source must include a pinned version, e.g. nuget:{parsed.Value}/1.2.3"]
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                SourceFetchResult fetchResult;
                try
                {
                    fetchResult = await _sourceFetcher.FetchAsync(parsed.Value, parsed.Version, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SourceNotAvailableException ex)
                {
                    _logger.LogWarning("Source fetch failed for {Source}: {Message}", trimmedSource, ex.Message);
                    await _emitter.EmitAsync(new ExtensionErrorEvent
                    {
                        Source = trimmedSource,
                        Phase = "sourceFetch",
                        Diagnostics = [$"Source not available: {ex.Message}", "Extension cannot be added because its source code cannot be verified."]
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                SecurityAnalysisReport report = await _securityAnalyser.AnalyseAsync(fetchResult, cancellationToken)
                    .ConfigureAwait(false);

                riskDisplay = report.RiskLevel.ToString().ToLowerInvariant();
            }

            // Idempotency: check if the normalized source is already present in this scope.
            string normalizedKey = ExtensionSourceNormalizer.Normalize(trimmedSource);
            IReadOnlyList<ExtensionEntry> existing = _configReader.Read(configPath);
            bool alreadyPresent = existing.Any(e =>
                string.Equals(ExtensionSourceNormalizer.Normalize(e.Source), normalizedKey, StringComparison.Ordinal));

            if (alreadyPresent)
            {
                await _emitter.EmitAsync(new SystemNoticeEvent
                {
                    Message = $"Extension '{trimmedSource}' is already in {scope} config. Run /reload to (re)activate it."
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Write to config (comment-preserving text edit — no YAML round-trip serializer).
            string directory = Path.GetDirectoryName(configPath)!;
            Directory.CreateDirectory(directory);

            string content;
            if (!File.Exists(configPath))
            {
                content = $"extensions:\n  - source: \"{trimmedSource}\"\n";
            }
            else
            {
                string existingText = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                content = InsertExtensionEntry(existingText, trimmedSource);
            }

            await File.WriteAllTextAsync(configPath, content, cancellationToken).ConfigureAwait(false);

            await _emitter.EmitAsync(new SystemNoticeEvent
            {
                Message = $"Extension '{trimmedSource}' added to {scope} config (risk: {riskDisplay}). Run /reload to activate."
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "extension.load failed for {Source}", trimmedSource);
            await _emitter.EmitAsync(new ExtensionErrorEvent
            {
                Source = trimmedSource,
                Phase = "write",
                Diagnostics = [ex.Message]
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task UnloadAsync(ExtensionUnloadCommand cmd, CancellationToken cancellationToken)
        // Removal is by deleting the entry from config and running /reload.
        => throw new NotImplementedException("extension.unload is not supported — remove the entry from config.yaml and run /reload.");

    public Task PromoteAsync(ExtensionPromoteCommand cmd, CancellationToken cancellationToken)
        // Promote has no meaning in the edit-only model; scope is chosen at add time.
        => throw new NotImplementedException("extension.promote is not supported in the edit-only model.");

    // Returns null for unknown scopes; callers treat null as an error.
    protected virtual string? ResolveConfigPath(string scope)
    {
        if (scope == "project")
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ".dmon", "config.yaml");
        }

        if (scope == "user")
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmon",
                "config.yaml");
        }

        return null;
    }

    // Splices a new list item directly beneath the active `extensions:` key.
    // If no active `extensions:` key exists (e.g. only the commented template),
    // appends a fresh block at end.
    private static string InsertExtensionEntry(string existing, string source)
    {
        string[] lines = existing.Split('\n');
        int extensionsIndex = Array.FindIndex(lines, IsTopLevelExtensionsLine);

        string newItem = $"  - source: \"{source}\"";

        if (extensionsIndex < 0)
        {
            return existing.TrimEnd() + $"\n\nextensions:\n{newItem}\n";
        }

        IEnumerable<string> head = lines.Take(extensionsIndex + 1);
        IEnumerable<string> tail = lines.Skip(extensionsIndex + 1);
        return string.Join('\n', head) + "\n" + newItem + "\n" + string.Join('\n', tail);
    }

    // An active (uncommented, column-zero) `extensions:` mapping key.
    private static bool IsTopLevelExtensionsLine(string line)
    {
        if (line.StartsWith('#') || line.Length == 0 || char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        return line.TrimEnd() == "extensions:";
    }

    // A source is writable only if it is non-empty and safe to embed inside a YAML
    // double-quoted scalar without escaping: no quotes, no newlines, no control characters.
    private static bool IsWritableSource(string source) =>
        !string.IsNullOrWhiteSpace(source) &&
        !source.Contains('"') &&
        !source.Any(char.IsControl);
}
