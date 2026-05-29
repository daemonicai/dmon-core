namespace Dmon.Memory.Meko;

/// <summary>
/// Controls when <c>RecordAsync</c> forwards conversation turns to Meko's
/// <c>memory_add(messages)</c> pipeline (D8). <c>AddFactAsync</c> is not affected
/// by this enum — it always calls <c>memory_add(text)</c>.
/// </summary>
public enum MekoCaptureMode
{
    /// <summary>
    /// Never forward turns to Meko (default). <c>RecordAsync</c> completes successfully
    /// without making any network call. Use this to run dmon with long-term memory
    /// disabled at the capture level.
    /// </summary>
    None,

    /// <summary>
    /// Forward every batch of turns as it arrives. Each <c>RecordAsync</c> call triggers
    /// a <c>memory_add(messages)</c> call immediately.
    /// </summary>
    EveryTurn,
}
