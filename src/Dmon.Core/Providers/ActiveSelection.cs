namespace Dmon.Core.Providers;

/// <summary>
/// The provider and optional model that were last explicitly selected by the user.
/// </summary>
public sealed record ActiveSelection(string Provider, string? Model);

/// <summary>
/// Persists and restores the active provider/model selection across restarts.
/// </summary>
public interface IActiveModelStore
{
    /// <summary>
    /// Reads the persisted selection. Returns null when no selection has been saved,
    /// the file is absent, or the file cannot be parsed.
    /// </summary>
    ActiveSelection? Load();

    /// <summary>
    /// Atomically writes the selection to the backing file, creating the .dmon
    /// directory if it does not yet exist.
    /// </summary>
    Task SaveAsync(ActiveSelection selection, CancellationToken cancellationToken = default);
}
