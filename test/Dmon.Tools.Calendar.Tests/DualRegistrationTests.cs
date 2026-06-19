using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Tools.Calendar.Tests;

public sealed class DualRegistrationTests
{
    [Fact]
    public void AddCalendarAbilities_RegistersToolExtension()
    {
        FakeToolRegistration reg = new();
        reg.AddCalendarAbilities();

        IEnumerable<IToolExtension> extensions = reg.Services.BuildServiceProvider().GetServices<IToolExtension>();
        List<AIFunction> tools = extensions.SelectMany(e => e.Tools).ToList();

        Assert.Contains(tools, t => t.Name == "lookup_calendar");
        Assert.Contains(tools, t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void AddCalendarAbilities_RegistersAbilityProvider()
    {
        FakeToolRegistration reg = new();
        reg.AddCalendarAbilities();

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        IEnumerable<IAbilityProvider> providers = sp.GetServices<IAbilityProvider>();
        AbilityRegistry registry = new(providers);
        IList<AITool> personalTools = registry.ForScope("personal");

        Assert.Contains(personalTools, t => t.Name == "lookup_calendar");
        Assert.Contains(personalTools, t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void AddCalendarAbilities_AbilityProviderNotLeakedIntoToolExtensions()
    {
        FakeToolRegistration reg = new();
        reg.AddCalendarAbilities();

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        // Only CalendarExtension should appear as IToolExtension — CalendarAbilityProvider must not.
        List<IToolExtension> toolExtensions = sp.GetServices<IToolExtension>().ToList();
        Assert.Single(toolExtensions);
        Assert.IsType<CalendarExtension>(toolExtensions[0]);

        // CalendarAbilityProvider should appear only as IAbilityProvider.
        List<IAbilityProvider> abilityProviders = sp.GetServices<IAbilityProvider>().ToList();
        Assert.Single(abilityProviders);
        Assert.IsType<CalendarAbilityProvider>(abilityProviders[0]);
    }
}

internal sealed class FakeToolRegistration : IToolRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
}
