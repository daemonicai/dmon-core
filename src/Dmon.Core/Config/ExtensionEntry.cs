namespace Dmon.Core.Config;

/// <summary>
/// One entry from the <c>extensions</c> list in a single <c>config.yaml</c> file.
/// <c>Source</c> is the only required field; <c>Settings</c> captures all other
/// sibling keys for use by the effective-set union/dedup step.
/// </summary>
public sealed record ExtensionEntry(
    string Source,
    IReadOnlyDictionary<string, string?> Settings);
