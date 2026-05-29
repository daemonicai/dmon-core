namespace Dmon.Abstractions.Memory;

/// <summary>
/// Ambient identity bound once per session and threaded through both memory tiers.
/// Carries the three identifiers that scope every memory operation; individual calls
/// carry only <see cref="MemoryScope"/> (per D9).
/// </summary>
/// <param name="DatapackId">
/// The datapack (workspace/project) identifier from configuration.
/// </param>
/// <param name="AgentId">
/// The agent identifier. Bound to <c>"dmon"</c> for the core agent (set when the
/// context is constructed).
/// </param>
/// <param name="ConversationId">
/// The current session identifier, equivalent to the session directory name.
/// Maps to <c>conversation_id</c> in Meko's scope model.
/// </param>
public sealed record MemoryContext(
    string DatapackId,
    string AgentId,
    string ConversationId);
