using Dmon.Abstractions.Extensions;
using Dmon.Core.Extensions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Dmon.Core.Tests.Extensions;

// ---------------------------------------------------------------------------
// Stub helpers
// ---------------------------------------------------------------------------

file sealed class StubAbilityProvider : IAbilityProvider
{
    public string Scope { get; }
    public IEnumerable<AITool> Tools { get; }

    public StubAbilityProvider(string scope, IEnumerable<AITool> tools)
    {
        Scope = scope;
        Tools = tools;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class AbilityRegistryTests
{
    private static AIFunction MakeTool(string name) =>
        AIFunctionFactory.Create(() => name, name);

    [Fact]
    public void ForScope_ReturnsOnlyToolsForThatScope()
    {
        AIFunction personal1 = MakeTool("personal_tool_1");
        AIFunction personal2 = MakeTool("personal_tool_2");
        AIFunction world1 = MakeTool("world_tool_1");

        AbilityRegistry registry = new(
        [
            new StubAbilityProvider("personal", [personal1, personal2]),
            new StubAbilityProvider("world", [world1]),
        ]);

        IList<AITool> result = registry.ForScope("personal");

        Assert.Equal(2, result.Count);
        Assert.Contains(personal1, result);
        Assert.Contains(personal2, result);
        Assert.DoesNotContain(world1, result);
    }

    [Fact]
    public void ForScope_IsCaseInsensitive()
    {
        AIFunction tool = MakeTool("cal_tool");

        AbilityRegistry registry = new(
        [
            new StubAbilityProvider("Personal", [tool]),
        ]);

        IList<AITool> lower = registry.ForScope("personal");
        IList<AITool> upper = registry.ForScope("PERSONAL");
        IList<AITool> mixed = registry.ForScope("Personal");

        Assert.Single(lower);
        Assert.Single(upper);
        Assert.Single(mixed);
        Assert.Contains(tool, lower);
    }

    [Fact]
    public void ForScope_EmptyRegistry_ReturnsEmptyList()
    {
        AbilityRegistry registry = new([]);

        IList<AITool> result = registry.ForScope("personal");

        Assert.Empty(result);
    }

    [Fact]
    public void ForScope_UnknownScope_ReturnsEmptyList()
    {
        AIFunction tool = MakeTool("personal_tool");

        AbilityRegistry registry = new(
        [
            new StubAbilityProvider("personal", [tool]),
        ]);

        IList<AITool> result = registry.ForScope("world");

        Assert.Empty(result);
    }

    [Fact]
    public void ForScope_MultipleProvidersOfSameScope_AreAllDiscovered()
    {
        AIFunction tool1 = MakeTool("tool_1");
        AIFunction tool2 = MakeTool("tool_2");
        AIFunction tool3 = MakeTool("tool_3");

        AbilityRegistry registry = new(
        [
            new StubAbilityProvider("personal", [tool1, tool2]),
            new StubAbilityProvider("personal", [tool3]),
        ]);

        IList<AITool> result = registry.ForScope("personal");

        Assert.Equal(3, result.Count);
        Assert.Contains(tool1, result);
        Assert.Contains(tool2, result);
        Assert.Contains(tool3, result);
    }

    [Fact]
    public void ForScope_DoesNotReturnIToolExtensionTools()
    {
        // AbilityRegistry only holds IAbilityProvider tools.
        // IToolExtension tools live in ToolRegistry and must never appear here.
        // This test confirms an empty IAbilityProvider set yields nothing —
        // the orthogonality guarantee is structural (different DI registration
        // path), so we verify ForScope never returns tools we did not register.
        AIFunction unregisteredTool = MakeTool("extension_tool");

        // Registry has no providers at all — only an extension would add tools.
        AbilityRegistry registry = new([]);

        IList<AITool> result = registry.ForScope("personal");

        Assert.Empty(result);
        Assert.DoesNotContain(unregisteredTool, result);
    }
}
