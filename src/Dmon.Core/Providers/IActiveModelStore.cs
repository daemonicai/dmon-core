using Dmon.Abstractions.Providers;

namespace Dmon.Core.Providers;

/// <summary>
/// Persists and restores the active provider/model selection across restarts.
/// </summary>
public interface IActiveModelStore
{
    /// <summary>
    /// Reads the persisted selection from the backing IConfiguration layer.
    /// Returns null when no selection has been saved or the value cannot be parsed.
    /// Never throws.
    /// </summary>
    ModelRef? Load();

    /// <summary>
    /// Atomically writes <paramref name="selection"/> to .dmon/config.local.yaml,
    /// preserving any other top-level keys already present in that file.
    /// Creates the .dmon directory if it does not yet exist.
    /// </summary>
    Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default);
}
