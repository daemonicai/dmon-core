using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Meko;

/// <summary>
/// Configuration for the Meko-backed long-term memory store.
/// Bind from configuration; never pass <see cref="ApiKey"/> through logs.
/// </summary>
public sealed class MekoLongTermOptions
{
    /// <summary>
    /// The Meko MCP endpoint. Defaults to the production endpoint.
    /// </summary>
    public Uri Endpoint { get; set; } = new("https://mcp.mekodata.ai/mcp");

    /// <summary>
    /// The Meko API key (<c>mko_tkn_…</c>). Required when long-term memory is enabled.
    /// Never logged.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The datapack (workspace/project) identifier scoped to this session.
    /// Maps to Meko's <c>datapack_id</c>; set from configuration.
    /// </summary>
    public string DatapackId { get; set; } = string.Empty;

    /// <summary>
    /// The dmon session identifier. Used as Meko's <c>run_id</c> when scope is
    /// <see cref="MemoryScope.Session"/>. The Meko <c>conversation_id</c>
    /// UUID is obtained separately via <c>conversation_create</c>.
    /// Set once per session by the host; never auto-generated here.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Controls when <see cref="ILongTermMemory.RecordAsync"/> forwards turns to Meko.
    /// Defaults to <see cref="MekoCaptureMode.None"/> so that no distillation cost is
    /// incurred unless explicitly opted in.
    /// <see cref="ILongTermMemory.AddFactAsync"/> is NOT affected by this setting.
    /// </summary>
    public MekoCaptureMode CaptureMode { get; set; } = MekoCaptureMode.None;
}
