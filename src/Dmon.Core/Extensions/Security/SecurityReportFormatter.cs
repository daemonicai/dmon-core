using System.Text;

namespace Dmon.Core.Extensions.Security;

public static class SecurityReportFormatter
{
    private const string Note = "Note: analysis covers extension source only; transitive dependencies are not analysed.";

    public static string Format(SecurityAnalysisReport report)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Analysing {report.PackageId} v{report.Version}...");
        sb.AppendLine();

        bool hasRisk = report.Findings.Any(f => f.Severity == FindingSeverity.Risk);
        bool hasFindings = report.Findings.Count > 0;

        if (!hasFindings)
        {
            sb.AppendLine("✅ No concerns found — source looks clean.");
            sb.AppendLine($"   Summary: {report.Summary}");
        }
        else if (!hasRisk)
        {
            sb.AppendLine($"⚠️  {report.Findings.Count} finding(s):");
            foreach (SecurityFinding finding in report.Findings)
                sb.AppendLine($"   [{SeverityLabel(finding.Severity)}] {finding.Description}");
        }
        else
        {
            sb.AppendLine($"🚨 {report.Findings.Count} finding(s):");
            foreach (SecurityFinding finding in report.Findings)
                sb.AppendLine($"   [{SeverityLabel(finding.Severity)}] {finding.Description}");
            sb.AppendLine($"   Summary: {report.Summary}");
        }

        sb.AppendLine();
        sb.Append(Note);

        return sb.ToString();
    }

    private static string SeverityLabel(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Info => "info",
        FindingSeverity.Warn => "warn",
        FindingSeverity.Risk => "risk",
        _ => "warn",
    };
}
