using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.5 / 6.1 / 6.3 — Scope and identity arg assertions (revised D9, live-verified 2026-05-29).
/// <list type="bullet">
///   <item><c>scope</c> is always <c>"admin"</c> (fixed constant, never an enum string).</item>
///   <item><c>conversation_id</c> is the Meko UUID from <c>conversation_create</c>
///     (the fake returns <see cref="FakeMekoToolInvoker.FakeConversationId"/>).</item>
///   <item><c>agent_id</c> is always <c>"dmon"</c>.</item>
///   <item><c>run_id</c> is present only for <see cref="MemoryScope.Session"/>; absent for
///     all durable scopes.</item>
///   <item><c>datapack_id</c> is absent when the configured value is not a UUID
///     (the default test options use <c>"dp-test"</c>, which is not a UUID).</item>
/// </list>
/// </summary>
public sealed class MekoScopeAndIdentityTests
{
    private static MekoLongTermMemory BuildWithContext(FakeMekoToolInvoker fake) =>
        MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.None);

    // Returns the first non-conversation_create call's args.
    private static IReadOnlyDictionary<string, object?> FirstMemoryCallArgs(FakeMekoToolInvoker fake)
    {
        var call = fake.Calls.First(c => !string.Equals(c.Tool, "conversation_create", StringComparison.Ordinal));
        return call.Args;
    }

    private static void AssertScopeAdmin(IReadOnlyDictionary<string, object?> args)
    {
        Assert.True(args.ContainsKey("scope"), "scope must always be present");
        Assert.Equal("admin", args["scope"]);
    }

    private static void AssertConversationIdIsFakeUuid(IReadOnlyDictionary<string, object?> args)
    {
        Assert.True(args.ContainsKey("conversation_id"), "conversation_id must always be present");
        Assert.Equal(FakeMekoToolInvoker.FakeConversationId, args["conversation_id"]);
    }

    private static void AssertAgentId(IReadOnlyDictionary<string, object?> args)
    {
        Assert.True(args.ContainsKey("agent_id"), "agent_id must always be present");
        Assert.Equal("dmon", args["agent_id"]);
    }

    private static void AssertNoDatapackId(IReadOnlyDictionary<string, object?> args)
    {
        // Default test options use "dp-test" which is not a UUID → must be omitted.
        Assert.False(args.ContainsKey("datapack_id"),
            "datapack_id must be omitted when the value is not a valid UUID (D9).");
    }

    private static void AssertAmbientIdentity(IReadOnlyDictionary<string, object?> args)
    {
        AssertScopeAdmin(args);
        AssertConversationIdIsFakeUuid(args);
        AssertAgentId(args);
        AssertNoDatapackId(args);
    }

    [Theory]
    [InlineData(MemoryScope.Agent)]
    [InlineData(MemoryScope.User)]
    [InlineData(MemoryScope.Shared)]
    public async Task SearchAsync_DurableScope_ScopeAdminNoRunId(MemoryScope scope)
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.SearchAsync("q", scope);

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertScopeAdmin(args);
        Assert.False(args.ContainsKey("run_id"),
            $"run_id must be absent for durable scope {scope} (D9).");
    }

    [Fact]
    public async Task SearchAsync_SessionScope_ScopeAdminRunIdIsSessionId()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"results\":[]}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.SearchAsync("q", MemoryScope.Session);

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertScopeAdmin(args);
        Assert.True(args.ContainsKey("run_id"), "run_id must be present for Session scope (D9).");
        // run_id is MekoScopeMapping.ToRunId(Session, sessionId) — for a GUID session id
        // the result is the "N" format (32 hex chars, no hyphens), safe for Meko's int(x,16).
        Assert.Equal(MekoTestHelpers.TestSessionIdNFormat, args["run_id"]);
    }

    [Fact]
    public async Task AddFactAsync_ScopeAdmin_NoRunId_AmbientIdentityPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.AddFactAsync("a fact");

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertAmbientIdentity(args);
        Assert.False(args.ContainsKey("run_id"),
            "run_id must be absent for default (Agent) scope.");
    }

    [Fact]
    public async Task GetAsync_ScopeAdmin_NoRunId_AmbientIdentityPresent()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{\"id\":\"x\",\"memory\":\"y\"}");
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.GetAsync("x");

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertScopeAdmin(args);
        AssertConversationIdIsFakeUuid(args);
        AssertAgentId(args);
        AssertNoDatapackId(args);
    }

    [Fact]
    public async Task UpdateAsync_ScopeAdmin_NoRunId_AmbientIdentityPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.UpdateAsync("id", "text");

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertScopeAdmin(args);
        AssertConversationIdIsFakeUuid(args);
        AssertAgentId(args);
        AssertNoDatapackId(args);
    }

    [Fact]
    public async Task DeleteAsync_ScopeAdmin_NoRunId_AmbientIdentityPresent()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.DeleteAsync("id");

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertScopeAdmin(args);
        AssertConversationIdIsFakeUuid(args);
        AssertAgentId(args);
        AssertNoDatapackId(args);
    }

    [Fact]
    public async Task RecordAsync_EveryTurn_ScopeAdmin_NoRunId_AmbientIdentityPresent()
    {
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake, MekoCaptureMode.EveryTurn);

        await memory.RecordAsync([new ChatMessage(ChatRole.User, "hi")]);

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        AssertAmbientIdentity(args);
        Assert.False(args.ContainsKey("run_id"),
            "run_id must be absent for default (Agent) scope.");
    }

    [Fact]
    public async Task ConversationCreate_IsCalledOnce_BeforeFirstMemoryOp()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.AddFactAsync("fact one");
        await memory.AddFactAsync("fact two");

        // conversation_create must be called exactly once.
        int createCount = fake.Calls.Count(c =>
            string.Equals(c.Tool, "conversation_create", StringComparison.Ordinal));
        Assert.Equal(1, createCount);
    }

    [Fact]
    public async Task ConversationCreate_UsesAdminScope_AndAgentId()
    {
        var fake = new FakeMekoToolInvoker();
        MekoLongTermMemory memory = BuildWithContext(fake);

        await memory.AddFactAsync("fact");

        var createCall = fake.Calls.First(c =>
            string.Equals(c.Tool, "conversation_create", StringComparison.Ordinal));
        Assert.Equal("admin", createCall.Args["scope"]);
        Assert.Equal("dmon", createCall.Args["agent_id"]);
    }

    [Fact]
    public async Task DatapackId_IsIncluded_WhenConfiguredAsUuid()
    {
        var fake = new FakeMekoToolInvoker();
        string uuidDatapack = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        MekoLongTermOptions options = MekoTestHelpers.DefaultOptions();
        options.DatapackId = uuidDatapack;
        MekoLongTermMemory memory = MekoTestHelpers.BuildMemory(fake, options);

        await memory.AddFactAsync("fact");

        IReadOnlyDictionary<string, object?> args = FirstMemoryCallArgs(fake);
        Assert.True(args.ContainsKey("datapack_id"),
            "datapack_id must be included when configured value is a valid UUID.");
        Assert.Equal(uuidDatapack, args["datapack_id"]);
    }
}
