using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// Produces an <see cref="IChatClient"/> for a captured, isolated provider registration.
/// Used by sub-agent tool extensions to obtain a scoped client without touching the
/// session's <c>IProviderRegistry</c>.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Creates and returns an <see cref="IChatClient"/> from the isolated provider
    /// registration captured at composition time.
    /// </summary>
    ValueTask<IChatClient> CreateAsync(CancellationToken cancellationToken = default);
}
