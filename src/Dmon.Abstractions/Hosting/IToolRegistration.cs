using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Facet of <see cref="IDmonHostBuilder"/> that accepts tool extension registrations.
/// </summary>
public interface IToolRegistration
{
    /// <summary>
    /// Gets the service collection used to register tool extension dependencies.
    /// </summary>
    IServiceCollection Services { get; }
}
