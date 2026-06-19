using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Core.Extensions;
using Dmon.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Tools.Dcal.Tests;

public sealed class DualRegistrationTests
{
    [Fact]
    public void AddDcalAbilities_RegistersToolExtension()
    {
        FakeToolRegistration reg = new();
        reg.AddDcalAbilities();

        IEnumerable<IToolExtension> extensions = reg.Services.BuildServiceProvider().GetServices<IToolExtension>();
        List<AIFunction> tools = extensions.SelectMany(e => e.Tools).ToList();

        Assert.Contains(tools, t => t.Name == "lookup_calendar");
        Assert.Contains(tools, t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void AddDcalAbilities_RegistersAbilityProvider()
    {
        FakeToolRegistration reg = new();
        reg.AddDcalAbilities();

        ServiceProvider sp = reg.Services.BuildServiceProvider();
        IEnumerable<IAbilityProvider> providers = sp.GetServices<IAbilityProvider>();
        AbilityRegistry registry = new(providers);
        IList<AITool> personalTools = registry.ForScope("personal");

        Assert.Contains(personalTools, t => t.Name == "lookup_calendar");
        Assert.Contains(personalTools, t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void AddDcalAbilities_AbilityProviderNotLeakedIntoToolExtensions()
    {
        FakeToolRegistration reg = new();
        reg.AddDcalAbilities();

        ServiceProvider sp = reg.Services.BuildServiceProvider();

        // Only DcalExtension should appear as IToolExtension — DcalAbilityProvider must not.
        List<IToolExtension> toolExtensions = sp.GetServices<IToolExtension>().ToList();
        Assert.Single(toolExtensions);
        Assert.IsType<DcalExtension>(toolExtensions[0]);

        // DcalAbilityProvider should appear only as IAbilityProvider.
        List<IAbilityProvider> abilityProviders = sp.GetServices<IAbilityProvider>().ToList();
        Assert.Single(abilityProviders);
        Assert.IsType<DcalAbilityProvider>(abilityProviders[0]);
    }
}

internal sealed class FakeToolRegistration : IToolRegistration
{
    public IServiceCollection Services { get; } = new ServiceCollection();
}
