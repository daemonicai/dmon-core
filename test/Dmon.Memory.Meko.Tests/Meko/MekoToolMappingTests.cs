using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.1 — Each <see cref="MekoLongTermMemory"/> method calls the expected
/// <c>memory_*</c> MCP tool with the expected arguments.
/// </summary>
public sealed class MekoToolMappingTests
{
    [Fact]
    public async Task AddFactAsync_Calls_MemoryAdd_With_Text()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.AddFactAsync("the sky is blue");

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_add", tool);
        Assert.Equal("the sky is blue", args["text"]);
        Assert.False(args.ContainsKey("messages"), "memory_add(text) must not include messages");
    }

    [Fact]
    public async Task RecordAsync_OptedIn_Calls_MemoryAdd_With_Messages()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi there"),
        };
        await memory.RecordAsync(turns);

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_add", tool);
        Assert.True(args.ContainsKey("messages"), "RecordAsync must include messages arg");
        Assert.False(args.ContainsKey("text"), "memory_add(messages) must not include text");
    }

    [Fact]
    public async Task SearchAsync_Calls_MemorySearch_With_QueryAndLimit()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.SearchAsync("find me something", limit: 5);

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_search", tool);
        Assert.Equal("find me something", args["query"]);
        Assert.Equal(5, args["limit"]);
    }

    [Fact]
    public async Task GetAsync_Calls_MemoryGetById_With_MemoryId()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"id\":\"abc\",\"memory\":\"some text\",\"score\":0.9}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.GetAsync("abc");

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_get_by_id", tool);
        Assert.Equal("abc", args["memory_id"]);
    }

    [Fact]
    public async Task ListAsync_Calls_MemoryGetAll_With_Scope()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.ListAsync(MemoryScope.User);

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_get_all", tool);
        Assert.Equal("user", args["scope"]);
    }

    [Fact]
    public async Task UpdateAsync_Calls_MemoryUpdate_With_IdAndText()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.UpdateAsync("id-1", "new text");

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_update", tool);
        Assert.Equal("id-1", args["memory_id"]);
        Assert.Equal("new text", args["text"]);
    }

    [Fact]
    public async Task DeleteAsync_Calls_MemoryDeleteById_With_MemoryId()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.DeleteAsync("id-to-delete");

        Assert.Equal(1, fake.CallCount);
        (string tool, IReadOnlyDictionary<string, object?> args) = fake.Calls[0];
        Assert.Equal("memory_delete_by_id", tool);
        Assert.Equal("id-to-delete", args["memory_id"]);
    }

    [Fact]
    public async Task FlushAsync_Calls_FlushPendingMemoryCandidates()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        await memory.FlushAsync().AsTask();

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("flush_pending_memory_candidates", fake.Calls[0].Tool);
    }
}
