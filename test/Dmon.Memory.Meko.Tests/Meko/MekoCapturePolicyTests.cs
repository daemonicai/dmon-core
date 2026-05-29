using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.3 — Opt-in capture policy via <see cref="MekoCaptureMode"/> (D8).
/// Default keeps nothing; opted-in mode calls <c>memory_add</c>.
/// <see cref="MekoLongTermMemory.AddFactAsync"/> always asserts regardless of policy.
/// </summary>
public sealed class MekoCapturePolicyTests
{
    [Fact]
    public async Task RecordAsync_DefaultCapture_MakesNoInvokerCall()
    {
        var fake = new FakeMekoToolInvoker();
        // Default options use MekoCaptureMode.None.
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        var turns = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await memory.RecordAsync(turns);

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task RecordAsync_DefaultCapture_CompletesSuccessfully_DoesNotThrow()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        var turns = new List<ChatMessage> { new(ChatRole.User, "anything") };
        // Must not throw — "nothing kept" is a normal outcome, not an error.
        await memory.RecordAsync(turns);
    }

    [Fact]
    public async Task RecordAsync_EveryTurnCapture_CallsMemoryAdd()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "message one"),
            new(ChatRole.Assistant, "reply one"),
        };
        await memory.RecordAsync(turns);

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("memory_add", fake.Calls[0].Tool);
    }

    [Fact]
    public async Task AddFactAsync_AlwaysCallsMemoryAdd_RegardlessOfCaptureModeNone()
    {
        var fake = new FakeMekoToolInvoker();
        // CaptureMode.None should NOT gate AddFactAsync.
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

        await memory.AddFactAsync("an explicit fact");

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("memory_add", fake.Calls[0].Tool);
        Assert.Equal("an explicit fact", fake.Calls[0].Args["text"]);
    }

    [Fact]
    public async Task AddFactAsync_AlwaysCallsMemoryAdd_RegardlessOfCaptureModeEveryTurn()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        await memory.AddFactAsync("another fact");

        // AddFactAsync is separate from RecordAsync; still calls memory_add(text).
        Assert.Equal(1, fake.CallCount);
        Assert.Equal("memory_add", fake.Calls[0].Tool);
        Assert.Equal("another fact", fake.Calls[0].Args["text"]);
        // And it must NOT include a "messages" key.
        Assert.False(fake.Calls[0].Args.ContainsKey("messages"));
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
        // Even when capture is opted in, an empty turn list must make no network call
        // (avoids a wasted hosted call with an empty messages array — N2).
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
