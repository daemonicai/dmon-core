using System.Runtime.CompilerServices;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions.Security;
using Dmon.Core.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace Dmon.Core.Tests.Extensions.Security;

public sealed class ExtensionSecurityAnalyserTests
{
    private static SourceFetchResult EmptySource(string packageId = "Test.Pkg", string version = "1.0.0")
        => new()
        {
            PackageId = packageId,
            Version = version,
            RepositoryUrl = "https://github.com/test/test",
            CommitSha = "abc123",
            SourceFiles = new Dictionary<string, string>
            {
                ["Foo.cs"] = "public class Foo {}",
            },
        };

    [Fact]
    public async Task AnalyseAsync_ValidJsonResponse_ReturnsCorrectReport()
    {
        const string json = """
            {
              "risk_level": "low",
              "findings": [
                { "severity": "info", "description": "Uses HttpClient for extension purpose." }
              ],
              "summary": "Source appears clean."
            }
            """;

        IProviderRegistry providers = new FakeProviderRegistry(new FakeChatClient(json));
        ExtensionSecurityAnalyser analyser = new(providers);

        SecurityAnalysisReport report = await analyser.AnalyseAsync(EmptySource());

        Assert.Equal(RiskLevel.Low, report.RiskLevel);
        Assert.Single(report.Findings);
        Assert.Equal(FindingSeverity.Info, report.Findings[0].Severity);
        Assert.Equal("Uses HttpClient for extension purpose.", report.Findings[0].Description);
        Assert.Equal("Source appears clean.", report.Summary);
        Assert.Equal("Test.Pkg", report.PackageId);
        Assert.Equal("1.0.0", report.Version);
    }

    [Fact]
    public async Task AnalyseAsync_HighRiskResponse_ReturnsHighRiskReport()
    {
        const string json = """
            {
              "risk_level": "high",
              "findings": [
                { "severity": "risk", "description": "Harvests environment variables matching *_KEY pattern." },
                { "severity": "warn", "description": "Spawns external process." }
              ],
              "summary": "Dangerous package."
            }
            """;

        IProviderRegistry providers = new FakeProviderRegistry(new FakeChatClient(json));
        ExtensionSecurityAnalyser analyser = new(providers);

        SecurityAnalysisReport report = await analyser.AnalyseAsync(EmptySource());

        Assert.Equal(RiskLevel.High, report.RiskLevel);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(FindingSeverity.Risk, report.Findings[0].Severity);
        Assert.Equal(FindingSeverity.Warn, report.Findings[1].Severity);
    }

    [Fact]
    public async Task AnalyseAsync_MalformedJsonResponse_ReturnsMediumRiskInconclusiveReport()
    {
        const string badResponse = "This is not JSON at all!";

        IProviderRegistry providers = new FakeProviderRegistry(new FakeChatClient(badResponse));
        ExtensionSecurityAnalyser analyser = new(providers);

        SecurityAnalysisReport report = await analyser.AnalyseAsync(EmptySource());

        Assert.Equal(RiskLevel.Medium, report.RiskLevel);
        Assert.Single(report.Findings);
        Assert.Equal(FindingSeverity.Warn, report.Findings[0].Severity);
        Assert.Contains("inconclusive", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyseAsync_EmptyResponse_ReturnsMediumRiskInconclusiveReport()
    {
        IProviderRegistry providers = new FakeProviderRegistry(new FakeChatClient(string.Empty));
        ExtensionSecurityAnalyser analyser = new(providers);

        SecurityAnalysisReport report = await analyser.AnalyseAsync(EmptySource());

        Assert.Equal(RiskLevel.Medium, report.RiskLevel);
        Assert.Single(report.Findings);
    }

    [Fact]
    public async Task AnalyseAsync_JsonWrappedInCodeFences_ParsesCorrectly()
    {
        const string fenced = """
            ```json
            { "risk_level": "medium", "findings": [], "summary": "ok" }
            ```
            """;

        IProviderRegistry providers = new FakeProviderRegistry(new FakeChatClient(fenced));
        ExtensionSecurityAnalyser analyser = new(providers);

        SecurityAnalysisReport report = await analyser.AnalyseAsync(EmptySource());

        Assert.Equal(RiskLevel.Medium, report.RiskLevel);
        Assert.Empty(report.Findings);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

file sealed class FakeChatClient(string response) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, response);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

file sealed class FakeProviderRegistry(IChatClient client) : IProviderRegistry
{
    public ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(client);

    public ProviderConfig GetCurrentConfig() => new()
    {
        Name = "fake",
        Adapter = "fake",
        Auth = new ProviderAuthConfig { Type = "none" },
    };

    public IReadOnlyList<ProviderConfig> GetAll() => [GetCurrentConfig()];
    public void SetProvider(string name) { }
    public void SetModel(string modelId) { }
    public void CycleProvider() { }
    public Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public void AddDynamicProvider(ProviderConfig config) { }
    public string? GetCurrentModelId() => null;
    public ProviderSwitchResult? CommitPendingSwitch() => null;
    public bool CurrentSupportsToolCalling => false;
    public bool CurrentSupportsReasoning => false;
}
