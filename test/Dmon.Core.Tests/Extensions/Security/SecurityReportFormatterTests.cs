using Dmon.Core.Extensions.Security;
using Xunit;

namespace Dmon.Core.Tests.Extensions.Security;

public sealed class SecurityReportFormatterTests
{
    private static SecurityAnalysisReport MakeReport(
        RiskLevel risk,
        IReadOnlyList<SecurityFinding>? findings = null,
        string summary = "Looks good.",
        string packageId = "My.Package",
        string version = "1.0.0")
        => new()
        {
            RiskLevel = risk,
            Findings = findings ?? [],
            Summary = summary,
            PackageId = packageId,
            Version = version,
        };

    [Fact]
    public void Format_EmptyFindings_UsesCheckmarkHeader()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Low);
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("✅", result);
        Assert.DoesNotContain("⚠️", result);
        Assert.DoesNotContain("🚨", result);
    }

    [Fact]
    public void Format_EmptyFindings_ShowsSummary()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Low, summary: "Source looks clean.");
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("Source looks clean.", result);
    }

    [Fact]
    public void Format_WarnOnlyFindings_UsesWarningHeader()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Medium,
        [
            new SecurityFinding { Severity = FindingSeverity.Warn, Description = "Uses HttpClient." },
        ]);
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("⚠️", result);
        Assert.DoesNotContain("✅", result);
        Assert.DoesNotContain("🚨", result);
    }

    [Fact]
    public void Format_WarnOnlyFindings_OmitsSummary()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Medium,
        [
            new SecurityFinding { Severity = FindingSeverity.Warn, Description = "Uses HttpClient." },
        ],
        summary: "Some summary.");
        string result = SecurityReportFormatter.Format(report);
        // Summary is omitted for warn-only to keep it brief
        Assert.DoesNotContain("Some summary.", result);
    }

    [Fact]
    public void Format_RiskFindings_UsesDangerHeader()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.High,
        [
            new SecurityFinding { Severity = FindingSeverity.Risk, Description = "Harvests API keys." },
        ]);
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("🚨", result);
        Assert.DoesNotContain("✅", result);
        Assert.DoesNotContain("⚠️", result);
    }

    [Fact]
    public void Format_RiskFindings_ShowsSummary()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.High,
        [
            new SecurityFinding { Severity = FindingSeverity.Risk, Description = "Harvests API keys." },
        ],
        summary: "This package is dangerous.");
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("This package is dangerous.", result);
    }

    [Fact]
    public void Format_AlwaysEndsWithNoteAboutTransitiveDependencies()
    {
        SecurityAnalysisReport clean = MakeReport(RiskLevel.Low);
        SecurityAnalysisReport warn = MakeReport(RiskLevel.Medium,
        [
            new SecurityFinding { Severity = FindingSeverity.Warn, Description = "w" },
        ]);
        SecurityAnalysisReport risk = MakeReport(RiskLevel.High,
        [
            new SecurityFinding { Severity = FindingSeverity.Risk, Description = "r" },
        ]);

        Assert.Contains("transitive dependencies", SecurityReportFormatter.Format(clean));
        Assert.Contains("transitive dependencies", SecurityReportFormatter.Format(warn));
        Assert.Contains("transitive dependencies", SecurityReportFormatter.Format(risk));
    }

    [Fact]
    public void Format_FindingCount_AppearsInHeader()
    {
        List<SecurityFinding> findings =
        [
            new SecurityFinding { Severity = FindingSeverity.Warn, Description = "a" },
            new SecurityFinding { Severity = FindingSeverity.Info, Description = "b" },
        ];
        SecurityAnalysisReport report = MakeReport(RiskLevel.Medium, findings);
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("2 finding(s)", result);
    }

    [Fact]
    public void Format_InfoFinding_ShowsInfoLabel()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Low,
        [
            new SecurityFinding { Severity = FindingSeverity.Info, Description = "Uses outbound HTTP." },
        ]);
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("[info]", result);
    }

    [Fact]
    public void Format_ContainsPackageIdAndVersion()
    {
        SecurityAnalysisReport report = MakeReport(RiskLevel.Low, packageId: "Acme.Tool", version: "2.3.4");
        string result = SecurityReportFormatter.Format(report);
        Assert.Contains("Acme.Tool", result);
        Assert.Contains("2.3.4", result);
    }
}
