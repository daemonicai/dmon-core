using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.4 — The disabled/no-op store (<see cref="DisabledLongTermMemory"/>) makes zero
/// MCP/invoker calls; writes no-op, reads return empty or null (3.5).
/// </summary>
public sealed class DisabledLongTermMemoryTests
{
    private static readonly ILongTermMemory Disabled = DisabledLongTermMemory.Instance;

    [Fact]
    public async Task RecordAsync_NoOps_DoesNotThrow()
    {
        var turns = new List<ChatMessage> { new(ChatRole.User, "hi") };
        await Disabled.RecordAsync(turns);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty()
    {
        IReadOnlyList<MemoryHit> hits = await Disabled.SearchAsync("anything");
        Assert.Empty(hits);
    }

    [Fact]
    public async Task FlushAsync_NoOps_DoesNotThrow()
    {
        await Disabled.FlushAsync().AsTask();
    }

    [Fact]
    public async Task AddFactAsync_NoOps_DoesNotThrow()
    {
        await Disabled.AddFactAsync("some fact");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull()
    {
        MemoryHit? hit = await Disabled.GetAsync("any-id");
        Assert.Null(hit);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty()
    {
        IReadOnlyList<MemoryHit> hits = await Disabled.ListAsync();
        Assert.Empty(hits);
    }

    [Fact]
    public async Task UpdateAsync_NoOps_DoesNotThrow()
    {
        await Disabled.UpdateAsync("id", "new text");
    }

    [Fact]
    public async Task DeleteAsync_NoOps_DoesNotThrow()
    {
        await Disabled.DeleteAsync("id");
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(DisabledLongTermMemory.Instance, DisabledLongTermMemory.Instance);
    }
}
