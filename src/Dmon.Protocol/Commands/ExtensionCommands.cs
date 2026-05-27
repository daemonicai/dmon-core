using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ExtensionLoadCommand : Command
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>
/// Deregisters the named extension's tools from the registry so they are no longer offered to
/// the LLM and emits <c>ExtensionUnloadedEvent</c>. The extension's assembly is NOT unloaded —
/// it remains resident in the core process until the core is restarted. To reclaim the memory,
/// restart the core. Re-loading the same extension after an unload succeeds without requiring
/// a process restart because the assembly is already resident.
/// </summary>
public sealed record ExtensionUnloadCommand : Command
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record ExtensionPromoteCommand : Command
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}