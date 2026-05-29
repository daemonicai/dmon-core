using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.5 — Scope mapping (<see cref="MemoryScope"/>→Meko scope string) is applied to
/// every call that carries scope, and <c>run_id</c> is NEVER present in any args (D9).
/// Ambient ids (<c>datapack_id</c>, <c>agent_id</c>, <c>conversation_id</c>) are
/// always present (D9).
/// </summary>
public sealed class MekoScopeAndIdentityTests
{
    private static MekoLongTermMemory BuildWithContext(FakeMekoToolInvoker fake) =>
        MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

    private static void AssertNoRunId(IReadOnlyDictionary<string, object?> args)
    {
        Assert.False(args.ContainsKey("run_id"),
            "run_id must never be set in any Meko tool call (D9).");
    }

    private static void AssertAmbientIds(IReadOnlyDictionary<string, object?> args)
    {
        Assert.True(args.ContainsKey("datapack_id"), "datapack_id must always be present");
        Assert.True(args.ContainsKey("agent_id"), "agent_id must always be present");
        Assert.True(args.ContainsKey("conversation_id"), "conversation_id must always be present");
        Assert.Equal("dp-test", args["datapack_id"]);
        Assert.Equal("dmon", args["agent_id"]);
        Assert.Equal("sess-test", args["conversation_id"]);
    }

    [Theory]
    [InlineData(MemoryScope.Agent, "agent")]
    [InlineData(MemoryScope.Session, "session")]
    [InlineData(MemoryScope.User, "user")]
    [InlineData(MemoryScope.Shared, "shared")]
    public async Task SearchAsync_ScopeMappedCorrectly(MemoryScope scope, string expectedScopeString)
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.SearchAsync("q", scope);

        Assert.Equal(expectedScopeString, fake.Calls[0].Args["scope"]);
    }

    [Theory]
    [InlineData(MemoryScope.Agent, "agent")]
    [InlineData(MemoryScope.User, "user")]
    public async Task ListAsync_ScopeMappedCorrectly(MemoryScope scope, string expectedScopeString)
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.ListAsync(scope);

        Assert.Equal(expectedScopeString, fake.Calls[0].Args["scope"]);
    }

    [Fact]
    public async Task SearchAsync_RunIdNeverPresent()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.SearchAsync("q");

        AssertNoRunId(fake.Calls[0].Args);
    }

    [Fact]
    public async Task AddFactAsync_RunIdNeverPresent_AmbientIdsPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.AddFactAsync("a fact");

        IReadOnlyDictionary<string, object?> args = fake.Calls[0].Args;
        AssertNoRunId(args);
        AssertAmbientIds(args);
    }

    [Fact]
    public async Task GetAsync_RunIdNeverPresent_AmbientIdsPresent()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"id\":\"x\",\"memory\":\"y\"}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.GetAsync("x");

        IReadOnlyDictionary<string, object?> args = fake.Calls[0].Args;
        AssertNoRunId(args);
        AssertAmbientIds(args);
    }

    [Fact]
    public async Task UpdateAsync_RunIdNeverPresent_AmbientIdsPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.UpdateAsync("id", "text");

        IReadOnlyDictionary<string, object?> args = fake.Calls[0].Args;
        AssertNoRunId(args);
        AssertAmbientIds(args);
    }

    [Fact]
    public async Task DeleteAsync_RunIdNeverPresent_AmbientIdsPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.DeleteAsync("id");

        IReadOnlyDictionary<string, object?> args = fake.Calls[0].Args;
        AssertNoRunId(args);
        AssertAmbientIds(args);
    }

    [Fact]
    public async Task RecordAsync_EveryTurn_RunIdNeverPresent_AmbientIdsPresent()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        await memory.RecordAsync([new ChatMessage(ChatRole.User, "hi")]);

        IReadOnlyDictionary<string, object?> args = fake.Calls[0].Args;
        AssertNoRunId(args);
        AssertAmbientIds(args);
    }
}
