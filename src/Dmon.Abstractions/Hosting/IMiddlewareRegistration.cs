using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Facet of <see cref="IDmonHostBuilder"/> that accepts middleware registrations.
/// </summary>
public interface IMiddlewareRegistration
{
    /// <summary>
    /// Gets the service collection used to register middleware dependencies.
    /// </summary>
    IServiceCollection Services { get; }
}
