using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmon.Core.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions.Security;

internal sealed class ExtensionSecurityAnalyser : IExtensionSecurityAnalyser
{
    private const string SystemPrompt = """
        You are a security analyst reviewing C# extension source code before it is loaded into a coding agent.

        Analyse the provided source code and return a JSON object with this exact shape:
        {
          "risk_level": "low" | "medium" | "high",
          "findings": [{ "severity": "info" | "warn" | "risk", "description": "..." }],
          "summary": "..."
        }

        Check for:
        - Filesystem access outside CWD subtree (e.g. Path.GetTempPath, Environment.GetFolderPath other than UserProfile, absolute paths outside the project)
        - Outbound network calls (HttpClient, WebClient, Socket) — flag as "info" if consistent with the extension's stated purpose, "risk" if unexpected
        - Process spawning (Process.Start, ProcessStartInfo)
        - Dynamic assembly loading (Assembly.Load, Activator.CreateInstance with external types, reflection on private members)
        - Credential or environment variable harvesting (Environment.GetEnvironmentVariable matching *KEY, *TOKEN, *SECRET, *PASSWORD patterns)
        - Obfuscated or machine-generated code that resists inspection

        Return ONLY the JSON object, no other text.
        """;

    private readonly IProviderRegistry _providers;

    public ExtensionSecurityAnalyser(IProviderRegistry providers)
    {
        _providers = providers;
    }

    public async Task<SecurityAnalysisReport> AnalyseAsync(
        SourceFetchResult source,
        CancellationToken cancellationToken = default)
    {
        IChatClient chatClient = await _providers.GetCurrentAsync(cancellationToken);

        string userMessage = BuildUserMessage(source.SourceFiles);

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, userMessage),
        ];

        ChatOptions options = new() { Temperature = 0f };

        ChatResponse response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
        string raw = response.Text ?? string.Empty;

        return ParseResponse(raw, source.PackageId, source.Version);
    }

    private static string BuildUserMessage(IReadOnlyDictionary<string, string> sourceFiles)
    {
        StringBuilder sb = new();
        foreach (KeyValuePair<string, string> file in sourceFiles)
        {
            sb.AppendLine($"File: {file.Key}");
            sb.AppendLine($"```{file.Value}```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static SecurityAnalysisReport ParseResponse(string raw, string packageId, string version)
    {
        try
        {
            // Strip markdown code fences if the LLM wrapped the response
            string json = raw.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = json.IndexOf('\n');
                int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline > 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            JsonNode? node = JsonNode.Parse(json);
            if (node is null)
                return InconclusiveReport(packageId, version);

            RiskLevel riskLevel = ParseRiskLevel(node["risk_level"]?.GetValue<string>());
            List<SecurityFinding> findings = ParseFindings(node["findings"]?.AsArray());
            string summary = node["summary"]?.GetValue<string>() ?? string.Empty;

            return new SecurityAnalysisReport
            {
                RiskLevel = riskLevel,
                Findings = findings,
                Summary = summary,
                PackageId = packageId,
                Version = version,
            };
        }
        catch (JsonException)
        {
            return InconclusiveReport(packageId, version);
        }
        catch (InvalidOperationException)
        {
            return InconclusiveReport(packageId, version);
        }
    }

    private static RiskLevel ParseRiskLevel(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        _ => RiskLevel.Medium,
    };

    private static List<SecurityFinding> ParseFindings(JsonArray? array)
    {
        if (array is null)
            return [];

        List<SecurityFinding> findings = new(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is null) continue;
            FindingSeverity severity = ParseSeverity(item["severity"]?.GetValue<string>());
            string description = item["description"]?.GetValue<string>() ?? string.Empty;
            findings.Add(new SecurityFinding { Severity = severity, Description = description });
        }
        return findings;
    }

    private static FindingSeverity ParseSeverity(string? value) => value?.ToLowerInvariant() switch
    {
        "info" => FindingSeverity.Info,
        "warn" => FindingSeverity.Warn,
        "risk" => FindingSeverity.Risk,
        _ => FindingSeverity.Warn,
    };

    private static SecurityAnalysisReport InconclusiveReport(string packageId, string version) =>
        new()
        {
            RiskLevel = RiskLevel.Medium,
            Findings =
            [
                new SecurityFinding
                {
                    Severity = FindingSeverity.Warn,
                    Description = "Security analysis produced an unreadable response — review manually before proceeding.",
                },
            ],
            Summary = "Analysis inconclusive.",
            PackageId = packageId,
            Version = version,
        };
}
