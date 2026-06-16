namespace Dmon.Core.Extensions.Security;

public sealed record SecurityAnalysisReport
{
    public required RiskLevel RiskLevel { get; init; }
    public required IReadOnlyList<SecurityFinding> Findings { get; init; }
    public required string Summary { get; init; }
    public required string PackageId { get; init; }
    public required string Version { get; init; }
}

public sealed record SecurityFinding
{
    public required FindingSeverity Severity { get; init; }
    public required string Description { get; init; }
}

public enum RiskLevel { Low, Medium, High }

public enum FindingSeverity { Info, Warn, Risk }
