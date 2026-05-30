using Dmon.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Extensions;

/// <summary>
/// Folds registered <see cref="IDmonMiddleware"/> instances over a base <see cref="IChatClient"/>
/// to produce the per-turn pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fold per turn, not once at startup:</b> <c>IProviderRegistry.GetCurrentAsync()</c>
/// returns a fresh client each turn — it rebuilds when the model or provider switches (the
/// switch is queued and committed at the next turn boundary). Folding once at startup over a
/// captured client would break model-switching: the wrapped client would be stale.
/// Middleware <em>instances</em> live for the lifetime of the process (D6 — no hot-reload);
/// only the <c>Wrap</c> call is repeated each turn. State held in the middleware instance
/// (e.g., an in-memory semantic cache) therefore persists across turns as intended.
/// </para>
/// </remarks>
public sealed class MiddlewarePipelineBuilder
{
    private readonly IMiddlewareRegistry _registry;
    private readonly IConfiguration _configuration;

    public MiddlewarePipelineBuilder(IMiddlewareRegistry registry, IConfiguration configuration)
    {
        _registry = registry;
        _configuration = configuration;
    }

    /// <summary>
    /// Applies all registered middleware to <paramref name="baseClient"/> and returns the
    /// resulting <see cref="IChatClient"/>. When no middleware is registered the base client
    /// is returned unchanged.
    /// </summary>
    /// <remarks>
    /// Middleware is sorted ascending by effective priority (lower = innermost / closer to
    /// provider, higher = outermost / closer to caller). Equal priorities use stable
    /// registration order as a tiebreaker.
    /// </remarks>
    public IChatClient Apply(IChatClient baseClient)
    {
        IReadOnlyList<IDmonMiddleware> all = _registry.GetAll();
        if (all.Count == 0)
        {
            return baseClient;
        }

        // Index preserves registration order for the stable tiebreaker.
        IEnumerable<(IDmonMiddleware Middleware, int RegistrationIndex)> indexed =
            all.Select((m, i) => (m, i));

        IOrderedEnumerable<(IDmonMiddleware Middleware, int RegistrationIndex)> ordered =
            indexed
                .OrderBy(x => EffectivePriority(x.Middleware))
                .ThenBy(x => x.RegistrationIndex);

        return ordered.Aggregate(baseClient, (inner, x) => x.Middleware.Wrap(inner));
    }

    /// <summary>
    /// Returns the effective priority for <paramref name="middleware"/>: the config override
    /// at <c>middleware:&lt;ClassName&gt;:priority</c> (case-insensitive) if present, otherwise
    /// the <see cref="DmonMiddlewareAttribute.Priority"/> value on the instance's type.
    /// </summary>
    private int EffectivePriority(IDmonMiddleware middleware)
    {
        string className = middleware.GetType().Name;
        string? configValue = _configuration[$"middleware:{className}:priority"];
        if (configValue is not null && int.TryParse(configValue, out int overridden))
        {
            return overridden;
        }

        DmonMiddlewareAttribute? attr = (DmonMiddlewareAttribute?)
            Attribute.GetCustomAttribute(middleware.GetType(), typeof(DmonMiddlewareAttribute));

        return attr?.Priority ?? 0;
    }
}
