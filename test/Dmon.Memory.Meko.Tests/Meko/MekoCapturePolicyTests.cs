using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Dmon.Protocol.Conversation;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.3 — Opt-in capture policy via <see cref="MekoCaptureMode"/> (D8).
/// Default keeps nothing; opted-in mode calls <c>memory_add</c>.
/// <see cref="MekoLongTermMemory.AddFactAsync"/> always asserts regardless of policy.
/// Note: <c>conversation_create</c> is called lazily before the first memory op — tests
/// that enable capture count it and assert by tool name, not raw call index.
/// </summary>
public sealed class MekoCapturePolicyTests
{
    [Fact]
    public async Task RecordAsync_DefaultCapture_MakesNoInvokerCall()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        var records = new List<MessageRecord>
        {
            new() { EntryId = "1", Timestamp = DateTimeOffset.UtcNow, Role = "user", Parts = [new TextPart { Text = "hello" }] },
        };
        await memory.RecordAsync(records);

        // CaptureMode.None must short-circuit before EnsureConversationAsync — no calls.
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task RecordAsync_DefaultCapture_CompletesSuccessfully_DoesNotThrow()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        var records = new List<MessageRecord>
        {
            new() { EntryId = "1", Timestamp = DateTimeOffset.UtcNow, Role = "user", Parts = [new TextPart { Text = "anything" }] },
        };
        await memory.RecordAsync(records);
    }

    [Fact]
    public async Task RecordAsync_EveryTurnCapture_CallsConversationCreateThenMemoryAdd()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        var records = new List<MessageRecord>
        {
            new() { EntryId = "1", Timestamp = DateTimeOffset.UtcNow, Role = "user", Parts = [new TextPart { Text = "message one" }] },
            new() { EntryId = "2", Timestamp = DateTimeOffset.UtcNow, Role = "assistant", Parts = [new TextPart { Text = "reply one" }] },
        };
        await memory.RecordAsync(records);

        Assert.Equal(2, fake.CallCount);
        Assert.Equal("conversation_create", fake.Calls[0].Tool);
        Assert.Equal("memory_add", fake.Calls[1].Tool);
    }

    [Fact]
    public async Task AddFactAsync_AlwaysCallsMemoryAdd_RegardlessOfCaptureModeNone()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        await memory.AddFactAsync("an explicit fact");

        Assert.Equal(2, fake.CallCount);
        Assert.Equal("conversation_create", fake.Calls[0].Tool);
        Assert.Equal("memory_add", fake.Calls[1].Tool);
        Assert.Equal("an explicit fact", fake.Calls[1].Args["text"]);
    }

    [Fact]
    public async Task AddFactAsync_AlwaysCallsMemoryAdd_RegardlessOfCaptureModeEveryTurn()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        await memory.AddFactAsync("another fact");

        Assert.Equal(2, fake.CallCount);
        Assert.Equal("conversation_create", fake.Calls[0].Tool);
        Assert.Equal("memory_add", fake.Calls[1].Tool);
        Assert.Equal("another fact", fake.Calls[1].Args["text"]);
        Assert.False(fake.Calls[1].Args.ContainsKey("messages"));
    }

    [Fact]
    public async Task RecordAsync_EmptyTurns_DefaultMode_CompletesWithoutCall()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        await memory.RecordAsync([]);

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task RecordAsync_EmptyTurns_EveryTurnMode_CompletesWithoutCall()
    {
        // Empty turn list short-circuits before EnsureConversationAsync — no calls.
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        await memory.RecordAsync([]);

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task CaptureModeDefault_IsNone()
    {
        var options = new MekoLongTermOptions();
        Assert.Equal(MekoCaptureMode.None, options.CaptureMode);
    }
}
