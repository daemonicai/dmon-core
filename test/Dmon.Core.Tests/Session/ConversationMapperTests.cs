using System.Text.Json;
using Dmon.Core.Session;
using Dmon.Protocol.Conversation;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Session;

public class ConversationMapperTests
{
    // -------------------------------------------------------------------------
    // 1. Round-trip: known part types survive write → record → replay
    // -------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_TextAndFunctionCall_SurviveViaRecord()
    {
        ChatMessage assistantMessage = new(ChatRole.Assistant,
        [
            new TextContent("Hello, world!"),
            new FunctionCallContent("call-1", "bash", new Dictionary<string, object?>
            {
                ["command"] = "ls -la"
            })
        ]);

        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(assistantMessage);

        MessageRecord record = new()
        {
            EntryId = "test-entry",
            Timestamp = DateTimeOffset.UtcNow,
            Role = role,
            Parts = parts
        };

        ChatMessage replayed = ConversationMapper.ToMessage(record);

        Assert.Equal("assistant", role);
        Assert.Equal(2, replayed.Contents.Count);

        TextContent text = Assert.IsType<TextContent>(replayed.Contents[0]);
        Assert.Equal("Hello, world!", text.Text);

        FunctionCallContent call = Assert.IsType<FunctionCallContent>(replayed.Contents[1]);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("bash", call.Name);
        Assert.NotNull(call.Arguments);
        Assert.True(call.Arguments.ContainsKey("command"));
        Assert.Equal("ls -la", Assert.IsType<JsonElement>(call.Arguments["command"]).GetString());
    }

    [Fact]
    public void RoundTrip_FunctionResult_SurvivesViaRecord()
    {
        ChatMessage toolMessage = new(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", "file listing output")
        ]);

        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(toolMessage);

        MessageRecord record = new()
        {
            EntryId = "test-entry-2",
            Timestamp = DateTimeOffset.UtcNow,
            Role = role,
            Parts = parts
        };

        ChatMessage replayed = ConversationMapper.ToMessage(record);

        Assert.Equal("tool", role);
        Assert.Single(replayed.Contents);

        FunctionResultContent result = Assert.IsType<FunctionResultContent>(replayed.Contents[0]);
        Assert.Equal("call-1", result.CallId);
        Assert.NotNull(result.Result);
        Assert.Equal("file listing output", Assert.IsType<JsonElement>(result.Result).GetString());
    }

    // -------------------------------------------------------------------------
    // 2. Unknown content: preserved opaquely in record; excluded from replay
    // -------------------------------------------------------------------------

    [Fact]
    public void UnknownContent_MapsToUnknownPart_AndIsExcludedFromReplay()
    {
        FakeAIContent unknown = new("sentinel-value");
        ChatMessage message = new(ChatRole.Assistant, [unknown]);

        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(message, producedBy: "test-producer");

        Assert.Single(parts);
        UnknownPart unknownPart = Assert.IsType<UnknownPart>(parts[0]);
        Assert.Equal("test-producer", unknownPart.ProducedBy);
        // Raw JSON must be non-empty — the original content is preserved
        Assert.NotEqual(JsonValueKind.Null, unknownPart.Raw.ValueKind);

        MessageRecord record = new()
        {
            EntryId = "entry-3",
            Timestamp = DateTimeOffset.UtcNow,
            Role = role,
            Parts = parts
        };

        // On replay, unknown parts must be excluded from the reconstructed ChatMessage contents
        ChatMessage replayed = ConversationMapper.ToMessage(record);
        Assert.Empty(replayed.Contents);
    }

    // -------------------------------------------------------------------------
    // 3. Non-replayable parts (reasoning, usage) excluded from replay
    // -------------------------------------------------------------------------

    [Fact]
    public void ReasoningAndUsageParts_ExcludedFromReplay()
    {
        MessageRecord record = new()
        {
            EntryId = "entry-4",
            Timestamp = DateTimeOffset.UtcNow,
            Role = "assistant",
            Parts =
            [
                new TextPart { Text = "answer" },
                new ReasoningPart { Text = "chain of thought" },
                new UsagePart { InputTokens = 100, OutputTokens = 50 }
            ]
        };

        ChatMessage replayed = ConversationMapper.ToMessage(record);

        // Only the TextPart should be replayed
        Assert.Single(replayed.Contents);
        TextContent text = Assert.IsType<TextContent>(replayed.Contents[0]);
        Assert.Equal("answer", text.Text);
    }

    // -------------------------------------------------------------------------
    // 4. No Microsoft.Extensions.AI type appears in serialized MessageRecord JSON
    // -------------------------------------------------------------------------

    [Fact]
    public void SerializedMessageRecord_ContainsNoMEAITypeNames()
    {
        ChatMessage message = new(ChatRole.Assistant,
        [
            new TextContent("test"),
            new FunctionCallContent("c1", "tool_name", new Dictionary<string, object?> { ["x"] = 1 }),
            new FunctionResultContent("c1", "result-value")
        ]);

        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(message);

        MessageRecord record = new()
        {
            EntryId = "entry-5",
            Timestamp = DateTimeOffset.UtcNow,
            Role = role,
            Parts = parts
        };

        // Serialize through the base type so polymorphic discriminators are emitted
        string json = JsonSerializer.Serialize<SessionLogLine>(record);

        Assert.DoesNotContain("Microsoft.Extensions.AI", json);
        Assert.DoesNotContain("ChatMessage", json);
        Assert.DoesNotContain("AIContent", json);
        Assert.DoesNotContain("FunctionCallContent", json);
        Assert.DoesNotContain("FunctionResultContent", json);
        Assert.DoesNotContain("TextContent", json);
    }

    // -------------------------------------------------------------------------
    // 5. Mapper never mints entryId or timestamp
    // -------------------------------------------------------------------------

    [Fact]
    public void ToParts_DoesNotMintEntryIdOrTimestamp()
    {
        // ToParts returns (role, parts) only — no entryId/timestamp in scope.
        // This test confirms the return type has exactly those two members.
        ChatMessage message = new(ChatRole.User, [new TextContent("hi")]);
        (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(message);

        Assert.Equal("user", role);
        Assert.Single(parts);
    }
}

/// <summary>A custom AIContent subtype used to test unknown-content handling.</summary>
file sealed class FakeAIContent : AIContent
{
    public string Value { get; }

    public FakeAIContent(string value)
    {
        Value = value;
    }
}
