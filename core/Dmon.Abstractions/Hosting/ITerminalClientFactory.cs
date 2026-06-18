using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Produces the terminal <see cref="IChatClient"/> from already-constructed DI services.
/// </summary>
/// <remarks>
/// <para>
/// Register exactly one implementation via <c>builder.UseTerminalClientFactory&lt;T&gt;()</c>
/// in <c>Dmon.cs</c>. When <c>DmonHostBuilder.Build()</c> runs, it resolves the single
/// registered <see cref="ITerminalClientFactory"/> and calls
/// <see cref="Create(IServiceProvider)"/> with the built host's service provider to obtain
/// the session's terminal <see cref="IChatClient"/>.
/// </para>
/// <para>
/// <see cref="Create"/> is synchronous because it composes already-constructed backends
/// resolved from <paramref name="services"/> — it does not perform I/O.
/// </para>
/// </remarks>
public interface ITerminalClientFactory
{
    /// <summary>
    /// Creates and returns the terminal <see cref="IChatClient"/> by composing services
    /// resolved from <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The host's built service provider. Must not be <c>null</c>.</param>
    /// <returns>The <see cref="IChatClient"/> to use for the terminal session.</returns>
    IChatClient Create(IServiceProvider services);
}
