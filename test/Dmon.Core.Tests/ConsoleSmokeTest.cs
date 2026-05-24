using System.Text.Json;

namespace Dmon.Core.Tests;

/// <summary>
/// End-to-end smoke test that launches the Dmon.Core process over stdio
/// and verifies it starts, responds to commands, and shuts down cleanly.
///
/// The process is shared across all tests in this class via
/// <see cref="CoreProcessFixture"/>: it starts once, all tests run against
/// the same process, then it is stopped once.
/// </summary>
public class ConsoleSmokeTest : IClassFixture<CoreProcessFixture>
{
    private readonly CoreProcessFixture _fixture;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConsoleSmokeTest(CoreProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CoreStartsAndRespondsToSessionList()
    {
        Assert.NotNull(_fixture.StandardOutput);
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        string cmdId = Guid.NewGuid().ToString("N");
        string cmd = JsonSerializer.Serialize(new { type = "session.list", id = cmdId }, JsonOptions);
        await _fixture.StandardInput!.WriteLineAsync(cmd);
        await _fixture.StandardInput.FlushAsync();

        string? respLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await _fixture.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(2));
            if (line is null) break;
            if (line.Contains("\"response\"") && line.Contains(cmdId))
            {
                respLine = line;
                break;
            }
        }

        Assert.NotNull(respLine);
        JsonDocument respDoc = JsonDocument.Parse(respLine);
        Assert.Equal("response", respDoc.RootElement.GetProperty("type").GetString());
        Assert.True(respDoc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task CoreRespondsToErrorOnMalformedCommand()
    {
        Assert.NotNull(_fixture.StandardOutput);
        Assert.True(_fixture.AgentReadyReceived, _fixture.FormatFailure("agentReady was never received"));

        await _fixture.StandardInput!.WriteLineAsync("{not valid json");
        await _fixture.StandardInput.FlushAsync();

        string? errorLine = null;
        for (int i = 0; i < 20; i++)
        {
            string? line = await _fixture.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(2));
            if (line is null) break;
            if (line.Contains("\"error\""))
            {
                errorLine = line;
                break;
            }
        }

        Assert.NotNull(errorLine);
        JsonDocument errorDoc = JsonDocument.Parse(errorLine);
        Assert.Equal("error", errorDoc.RootElement.GetProperty("type").GetString());
    }
}
