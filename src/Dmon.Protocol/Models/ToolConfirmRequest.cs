using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Models;

public sealed record ToolConfirmRequest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, object?> Args { get; init; }
    public RiskLevel Risk { get; init; }
}
