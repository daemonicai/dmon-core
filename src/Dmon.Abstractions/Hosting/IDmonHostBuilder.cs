using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Aggregates the three registration facets and exposes the DI container and
/// configuration manager for direct use by hosting verbs.
/// </summary>
/// <remarks>
/// <para>
/// Verbs are extension methods in the <c>Dmon.Hosting</c> namespace that operate on
/// <see cref="IProviderRegistration"/>, <see cref="IToolRegistration"/>, or
/// <see cref="IMiddlewareRegistration"/>. Because <see cref="IDmonHostBuilder"/>
/// implements all three, calling a verb directly on the builder returns the builder
/// type (flat chaining). Calling a verb on a bare facet returns the facet (sub-agent reuse).
/// </para>
/// </remarks>
public interface IDmonHostBuilder : IProviderRegistration, IToolRegistration, IMiddlewareRegistration
{
    /// <summary>
    /// Gets the service collection used to register dependencies for the host.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Gets the configuration manager that aggregates all configuration sources.
    /// </summary>
    IConfigurationManager Configuration { get; }
}
