using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

public sealed class MekoScopeMappingTests
{
    [Theory]
    [InlineData(MemoryScope.Agent, "agent")]
    [InlineData(MemoryScope.Session, "session")]
    [InlineData(MemoryScope.User, "user")]
    [InlineData(MemoryScope.Shared, "shared")]
    public void ToMekoScope_KnownScope_ReturnsMappedString(MemoryScope scope, string expected)
    {
        string actual = MekoScopeMapping.ToMekoScope(scope);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToMekoScope_UnknownScope_FallsBackToAgent()
    {
        // Cast an out-of-range value to exercise the defensive fallback.
        MemoryScope unknown = (MemoryScope)999;
        string actual = MekoScopeMapping.ToMekoScope(unknown);
        Assert.Equal("agent", actual);
    }

    [Fact]
    public void ToMekoScope_DefaultScope_IsAgent()
    {
        // The default enum value is Agent (0); must map to "agent".
        Assert.Equal("agent", MekoScopeMapping.ToMekoScope(default));
    }
}
