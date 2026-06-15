using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Facet of <see cref="IDmonHostBuilder"/> that accepts provider registrations.
/// </summary>
public interface IProviderRegistration
{
    /// <summary>
    /// Gets the service collection used to register provider dependencies.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Gets the configuration manager used to set provider and model options.
    /// </summary>
    IConfigurationManager Configuration { get; }
}
