namespace Dmon.Extensions;

/// <summary>
/// Marks a class as a dmon middleware extension and supplies its default pipeline position.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline ordering:</b> lower <see cref="Priority"/> values are innermost (closer to
/// the base provider); higher values are outermost (closer to the caller). The default
/// value of <c>0</c> places middleware at the innermost position unless a higher value is
/// assigned. Space values to allow insertion (e.g., 100, 200, 300). When two middlewares
/// share the same effective priority the stable registration order acts as a tiebreaker.
/// The effective priority may be overridden per-registration on the builder, or via the
/// config key <c>middleware:&lt;ClassName&gt;:priority</c> — both beat the attribute.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DmonMiddlewareAttribute : Attribute
{
    /// <summary>
    /// Gets or initialises the pipeline position for this middleware.
    /// Lower values are innermost (closer to the provider); higher values are outermost
    /// (closer to the caller). Defaults to <c>0</c>.
    /// </summary>
    public int Priority { get; init; } = 0;
}
