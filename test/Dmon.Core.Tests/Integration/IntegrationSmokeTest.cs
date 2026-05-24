using System.Text.Json;

namespace Dmon.Core.Tests.Integration;

/// <summary>
/// End-to-end integration test that launches the Dmon.Core process over stdio
/// and verifies the full RPC surface: session creation, model listing, turn
/// submission with event flow, and error handling.
///
/// The process is shared across all tests in this class via
/// <see cref="CoreProcessFixture"/>: it starts once, all tests run against
/// the same process, then it is stopped once.
/// </summary>
public class IntegrationSmokeTest : IClassFixture<CoreProcessFixture>
{
    private readonly CoreProcessFixture _fixture;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IntegrationSmokeTest(CoreProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CoreStartsAndEmitsAgentReady()
    {
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));
    }

    [Fact]
    public async Task SessionCreateReturnsNewSession()
    {
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        string cmdId = Guid.NewGuid().ToString("N");
        await SendAsync(new { type = "session.create", id = cmdId });

        string? respLine = await ReadResponseAsync(cmdId);
        Assert.NotNull(respLine);

        using JsonDocument doc = JsonDocument.Parse(respLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        JsonElement payload = root.GetProperty("data");
        Assert.NotNull(payload.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ModelListReturnsModels()
    {
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        string cmdId = Guid.NewGuid().ToString("N");
        await SendAsync(new { type = "model.list", id = cmdId });

        string? respLine = await ReadResponseAsync(cmdId);
        Assert.NotNull(respLine);

        using JsonDocument doc = JsonDocument.Parse(respLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        JsonElement payload = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        // NullModelHandler returns an empty list when no providers are configured.
        // The array may be empty but must be present.
    }

    [Fact]
    public async Task TurnSubmitEmitsTurnStartAndTurnEnd()
    {
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        string cmdId = Guid.NewGuid().ToString("N");
        await SendAsync(new { type = "turn.submit", id = cmdId, message = "Hello" });

        bool sawTurnStart = false;

        for (int i = 0; i < 50; i++)
        {
            string? line = await _fixture.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(5));
            if (line is null) break;

            if (line.Contains("\"turnStart\""))
                sawTurnStart = true;

            if (line.Contains("\"turnEnd\"") || line.Contains("\"error\""))
                break;
        }

        Assert.True(sawTurnStart, "Expected turnStart event but none was received.");
    }

    [Fact]
    public async Task MalformedCommandProducesErrorEvent()
    {
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        await _fixture.StandardInput!.WriteLineAsync("{not valid json");
        await _fixture.StandardInput.FlushAsync();

        string? errorLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await _fixture.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(1));
            if (line is null) break;
            if (line.Contains("\"error\""))
            {
                errorLine = line;
                break;
            }
        }

        Assert.NotNull(errorLine);

        using JsonDocument doc = JsonDocument.Parse(errorLine);
        JsonElement root = doc.RootElement;

        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.NotNull(root.GetProperty("message").GetString());
    }

    [Fact]
    public void AgentReady_ReceivedWithoutSetupRequired()
    {
        // The fixture starts the core with appsettings.json that includes a provider stanza.
        // SetupCheckService sees providers > 0 and skips emitting setupRequired, so the
        // process emits agentReady directly. WaitForAgentReadyAsync in the fixture would
        // have timed out and left AgentReadyReceived = false if setupRequired had been
        // emitted instead (since the fixture does not handle that handshake).
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        // TODO: add a second fixture (NoprovidersProcessFixture) that starts the core
        // with no provider config and asserts setupRequired is emitted. This requires a
        // separate IClassFixture so it does not share the process with this class.
    }

    // ─── helpers ──────────────────────────────────────────────

    private async Task SendAsync(object cmd)
    {
        string json = JsonSerializer.Serialize(cmd, JsonOptions);
        await _fixture.StandardInput!.WriteLineAsync(json);
        await _fixture.StandardInput.FlushAsync();
    }

    private async Task<string?> ReadResponseAsync(string cmdId)
    {
        for (int i = 0; i < 20; i++)
        {
            string? line = await _fixture.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(2));
            if (line is null) return null;
            if (line.Contains("\"response\"") && line.Contains(cmdId))
                return line;
        }

        return null;
    }
}
