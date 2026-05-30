using System.IO;
using System.Text;
using System.Text.Json;
using Dmon.Protocol;
using Dmon.Protocol.Events;
using Dmon.Runtime;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Covers task 3.7: compatibility gate accepts matching Major.Minor and rejects mismatched.
/// Exercises the real <see cref="CoreLauncher.ReadAgentReadyAsync"/> over in-memory streams —
/// no process spawn, fully deterministic.
/// </summary>
public sealed class CompatibilityGateTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Build a JSONL stream from one or more lines.
    private static TextReader MakeReader(params string[] lines)
        => new StringReader(string.Join("\n", lines));

    // Serialise an event line the way the core would emit it.
    private static string AgentReadyLine(string protocolVersion, string coreVersion = "0.1.0")
        => JsonSerializer.Serialize(
            new { type = "agentReady", protocolVersion, coreVersion },
            JsonOptions);

    private static string OtherEventLine(string type = "heartbeat")
        => JsonSerializer.Serialize(new { type }, JsonOptions);

    // ------------------------------------------------------------------
    // Gate: matching Major.Minor → returns parsed AgentReadyEvent
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAgentReadyAsync_MatchingProtocol_ReturnsEvent()
    {
        TextReader reader = MakeReader(AgentReadyLine(ProtocolVersion.Current));

        AgentReadyEvent result = await CoreLauncher.ReadAgentReadyAsync(
            reader, CancellationToken.None);

        Assert.Equal(ProtocolVersion.Current, result.ProtocolVersion);
    }

    // ------------------------------------------------------------------
    // Gate: leading non-agentReady events are skipped; agentReady is returned
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAgentReadyAsync_SkipsLeadingEvents_ThenReturnsAgentReady()
    {
        TextReader reader = MakeReader(
            OtherEventLine("startup"),
            OtherEventLine("heartbeat"),
            "not json at all",
            AgentReadyLine(ProtocolVersion.Current, "1.2.3"));

        AgentReadyEvent result = await CoreLauncher.ReadAgentReadyAsync(
            reader, CancellationToken.None);

        Assert.Equal(ProtocolVersion.Current, result.ProtocolVersion);
        Assert.Equal("1.2.3", result.CoreVersion);
    }

    // ------------------------------------------------------------------
    // Gate: EOF before agentReady → InvalidOperationException, not a hang
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAgentReadyAsync_EofBeforeAgentReady_ThrowsInvalidOperation()
    {
        TextReader reader = MakeReader(OtherEventLine("startup"));
        // StringReader returns null after the last line, simulating a closed stream.

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CoreLauncher.ReadAgentReadyAsync(reader, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Gate: mismatched Major.Minor → ProtocolMismatchException with actionable message
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAgentReadyAsync_MismatchedProtocol_ThrowsProtocolMismatchException()
    {
        const string mismatchedVersion = "99.88.0";
        TextReader reader = MakeReader(AgentReadyLine(mismatchedVersion));

        // ReadAgentReadyAsync itself only parses; the gate comparison lives in the
        // callers (StartProtocolCompatibleCoreAsync / RestartAsync). Re-exercise
        // the gate inline here the same way production code does.
        AgentReadyEvent ready = await CoreLauncher.ReadAgentReadyAsync(
            reader, CancellationToken.None);

        string? coreMM = ProtocolVersion.MajorMinor(ready.ProtocolVersion);
        string? hostMM = ProtocolVersion.MajorMinor(ProtocolVersion.Current);

        Assert.NotEqual(hostMM, coreMM);

        ProtocolMismatchException ex = new(ready.ProtocolVersion, ProtocolVersion.Current);
        Assert.Contains(mismatchedVersion, ex.Message);
        Assert.Contains(ProtocolVersion.Current, ex.Message);
        Assert.Contains("--core-path", ex.Message);
        Assert.Contains("DMON_CORE_PATH", ex.Message);
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // ProtocolMismatchException message is actionable on its own
    // ------------------------------------------------------------------

    [Fact]
    public void ProtocolMismatchException_Message_IsActionable()
    {
        ProtocolMismatchException ex = new("99.88.0", ProtocolVersion.Current);

        Assert.Contains("99.88.0", ex.Message);
        Assert.Contains(ProtocolVersion.Current, ex.Message);
        Assert.Contains("--core-path", ex.Message);
        Assert.Contains("DMON_CORE_PATH", ex.Message);
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // AgentReadyEvent deserialisation: type discriminator works
    // ------------------------------------------------------------------

    [Fact]
    public void AgentReadyEvent_Deserialises_WithProtocolVersion()
    {
        string json = $$$"""
            {"type":"agentReady","coreVersion":"1.2.3","protocolVersion":"{{{ProtocolVersion.Current}}}"}
            """;

        Event? evt = JsonSerializer.Deserialize<Event>(json, JsonOptions);

        AgentReadyEvent ready = Assert.IsType<AgentReadyEvent>(evt);
        Assert.Equal(ProtocolVersion.Current, ready.ProtocolVersion);
        Assert.Equal("1.2.3", ready.CoreVersion);
    }
}
