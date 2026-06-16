using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.1 / 6.1 / 6.4 — Each <see cref="MekoLongTermMemory"/> method calls the expected
/// <c>memory_*</c> MCP tool with the expected arguments (revised args per live-verified
/// schema: <c>scope="admin"</c>, <c>conversation_id</c> from <c>conversation_create</c>,
/// <c>messages</c>/<c>metadata</c> as JSON strings).
/// </summary>
public sealed class MekoToolMappingTests
{
    // Returns the first non-conversation_create call.
    private static (string Tool, IReadOnlyDictionary<string, object?> Args) FirstMemoryCall(FakeMekoToolInvoker fake)
    {
        return fake.Calls.First(c => !string.Equals(c.Tool, "conversation_create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddFactAsync_Calls_MemoryAdd_With_Text_And_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.AddFactAsync("the sky is blue");

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_add", tool);
        Assert.Equal("the sky is blue", args["text"]);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
        Assert.False(args.ContainsKey("messages"), "memory_add(text) must not include messages");
    }

    [Fact]
    public async Task RecordAsync_OptedIn_Calls_MemoryAdd_With_MessagesAsJsonString()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi there"),
        };
        await memory.RecordAsync(turns);

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_add", tool);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
        Assert.True(args.ContainsKey("messages"), "RecordAsync must include messages arg");
        Assert.False(args.ContainsKey("text"), "memory_add(messages) must not include text");

        // messages must be a JSON string (6.4), not a structured object.
        string messagesArg = Assert.IsType<string>(args["messages"]);
        using JsonDocument doc = JsonDocument.Parse(messagesArg);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());

        JsonElement first = root[0];
        Assert.Equal("user", first.GetProperty("role").GetString());
        Assert.Equal("hello", first.GetProperty("content").GetString());

        JsonElement second = root[1];
        Assert.Equal("assistant", second.GetProperty("role").GetString());
        Assert.Equal("hi there", second.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SearchAsync_Calls_MemorySearch_With_QueryAndLimit_And_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.SearchAsync("find me something", limit: 5);

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_search", tool);
        Assert.Equal("find me something", args["query"]);
        Assert.Equal(5, args["limit"]);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    [Fact]
    public async Task GetAsync_Calls_MemoryGetById_With_MemoryId_And_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"id\":\"abc\",\"memory\":\"some text\",\"score\":0.9}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.GetAsync("abc");

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_get_by_id", tool);
        Assert.Equal("abc", args["memory_id"]);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    [Fact]
    public async Task ListAsync_Calls_MemoryGetAll_With_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.ListAsync(MemoryScope.User);

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_get_all", tool);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    [Fact]
    public async Task UpdateAsync_Calls_MemoryUpdate_With_IdAndText_And_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.UpdateAsync("id-1", "new text");

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_update", tool);
        Assert.Equal("id-1", args["memory_id"]);
        Assert.Equal("new text", args["text"]);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    [Fact]
    public async Task DeleteAsync_Calls_MemoryDeleteById_With_MemoryId_And_AdminScope()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.DeleteAsync("id-to-delete");

        var (tool, args) = FirstMemoryCall(fake);
        Assert.Equal("memory_delete_by_id", tool);
        Assert.Equal("id-to-delete", args["memory_id"]);
        Assert.Equal("admin", args["scope"]);
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    [Fact]
    public async Task FlushAsync_Makes_Zero_Invoker_Calls_And_Completes()
    {
        // flush_pending_memory_candidates is an agent-directive, not a server write (D7/D8,
        // verified live 2026-05-30). FlushAsync is a best-effort no-op and must not call
        // any MCP tool — capture happens at RecordAsync.
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.FlushAsync().AsTask();

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task ConversationCreate_IsSentFirst_BeforeMemoryAdd()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.AddFactAsync("fact");

        Assert.Equal(2, fake.CallCount);
        Assert.Equal("conversation_create", fake.Calls[0].Tool);
        Assert.Equal("memory_add", fake.Calls[1].Tool);
    }
}
