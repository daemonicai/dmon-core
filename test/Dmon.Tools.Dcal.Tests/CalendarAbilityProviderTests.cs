using Dmon.Core.Extensions;

namespace Dmon.Tools.Dcal.Tests;

public sealed class CalendarAbilityProviderTests
{
    [Fact]
    public void Scope_IsPersonal()
    {
        DcalAbilityProvider provider = new();
        Assert.Equal("personal", provider.Scope);
    }

    [Fact]
    public void ForScope_Personal_ContainsLookupCalendar()
    {
        AbilityRegistry registry = new([new DcalAbilityProvider()]);
        Assert.Contains(registry.ForScope("personal"), t => t.Name == "lookup_calendar");
    }

    [Fact]
    public void ForScope_Personal_ContainsListUpcomingEvents()
    {
        AbilityRegistry registry = new([new DcalAbilityProvider()]);
        Assert.Contains(registry.ForScope("personal"), t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void ForScope_World_DoesNotContainCalendarTools()
    {
        AbilityRegistry registry = new([new DcalAbilityProvider()]);
        System.Collections.Generic.IList<Microsoft.Extensions.AI.AITool> tools = registry.ForScope("world");
        Assert.DoesNotContain(tools, t => t.Name == "lookup_calendar");
        Assert.DoesNotContain(tools, t => t.Name == "list_upcoming_events");
    }
}
