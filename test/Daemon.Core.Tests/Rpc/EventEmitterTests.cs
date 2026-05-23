using System.Text;
using System.Text.Json;
using Daemon.Core.Rpc;
using Daemon.Protocol.Events;

namespace Daemon.Core.Tests.Rpc;

public sealed class EventEmitterTests
{
    [Fact]
    public async Task EmitAsync_WritesLfTerminatedLine()
    {
        StringBuilder sb = new();
        StringWriter writer = new(sb);
        EventEmitter emitter = new(writer);

        await emitter.EmitAsync(new AgentReadyEvent
        {
            ProtocolVersion = "1.0",
            CoreVersion = "0.1"
        });

        string output = sb.ToString();
        Assert.EndsWith("\n", output);
        // Exactly one line (no embedded newlines beyond the terminator).
        Assert.Single(output.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task EmitAsync_IncludesTypeDiscriminator()
    {
        StringBuilder sb = new();
        StringWriter writer = new(sb);
        EventEmitter emitter = new(writer);

        await emitter.EmitAsync(new AgentReadyEvent
        {
            ProtocolVersion = "1.0",
            CoreVersion = "0.1"
        });

        string line = sb.ToString().TrimEnd('\n', '\r');
        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.True(doc.RootElement.TryGetProperty("type", out JsonElement typeElem));
        Assert.Equal("agentReady", typeElem.GetString());
    }

    [Fact]
    public async Task EmitAsync_ConcurrentCalls_DoNotInterleave()
    {
        // Write many events concurrently and verify each line is valid JSON
        // with a "type" field — i.e. no lines were interleaved.
        StringBuilder sb = new();
        StringWriter writer = new(sb);
        EventEmitter emitter = new(writer);

        const int count = 50;
        Task[] tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            tasks[i] = emitter.EmitAsync(new AgentReadyEvent
            {
                ProtocolVersion = "1.0",
                CoreVersion = "0.1"
            });
        }

        await Task.WhenAll(tasks);

        string[] lines = sb.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(count, lines.Length);
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("type", out JsonElement typeElem));
            Assert.Equal("agentReady", typeElem.GetString());
        }
    }
}
