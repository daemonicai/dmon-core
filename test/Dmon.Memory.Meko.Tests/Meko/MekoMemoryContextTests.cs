using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

public sealed class MekoMemoryContextTests
{
    [Fact]
    public void Context_AgentId_IsAlwaysDmon()
    {
        var options = new MekoLongTermOptions
        {
            DatapackId = "dp-1",
            SessionId = "sess-abc",
        };

        var ctx = new MekoMemoryContext(options);

        Assert.Equal("dmon", ctx.Context.AgentId);
    }

    [Fact]
    public void Context_DatapackId_CopiedFromOptions()
    {
        var options = new MekoLongTermOptions { DatapackId = "my-datapack", SessionId = "s" };
        var ctx = new MekoMemoryContext(options);
        Assert.Equal("my-datapack", ctx.Context.DatapackId);
    }

    [Fact]
    public void Context_ConversationId_IsSessionId()
    {
        var options = new MekoLongTermOptions { DatapackId = "dp", SessionId = "session-xyz" };
        var ctx = new MekoMemoryContext(options);
        Assert.Equal("session-xyz", ctx.Context.ConversationId);
    }

    [Fact]
    public void Context_RunId_IsNotPresent()
    {
        // MemoryContext record must NOT have a RunId property (D9 — run_id is never set).
        var ctx = new MekoMemoryContext(new MekoLongTermOptions { DatapackId = "d", SessionId = "s" });
        var properties = ctx.Context.GetType().GetProperties();
        Assert.DoesNotContain(properties, p => p.Name.Equals("RunId", StringComparison.OrdinalIgnoreCase));
    }
}
