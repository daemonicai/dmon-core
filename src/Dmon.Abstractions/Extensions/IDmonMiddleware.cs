using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Extensions;

/// <summary>
/// Defines the contract for a dmon middleware extension.
/// Middleware wraps the <see cref="IChatClient"/> pipeline, intercepting every request
/// and response unconditionally — enabling capabilities such as semantic caching,
/// context-window management, RAG injection, guardrails, and observability.
/// </summary>
/// <remarks>
/// <para>
/// Register middleware through the <c>DmonHostBuilder</c> in <c>Dmon.cs</c> via
/// <c>AddMiddleware&lt;T&gt;()</c> or the instance overload. Middleware is a
/// compile-time dependency of <c>Dmon.cs</c>, not discovered at runtime.
/// </para>
/// <para>
/// To implement middleware, return a new <see cref="IChatClient"/> from
/// <see cref="Wrap"/> that delegates to <paramref name="inner"/> — typically by
/// subclassing <c>Microsoft.Extensions.AI.DelegatingChatClient</c>.
/// </para>
/// <para>
/// <see cref="DmonMiddlewareAttribute"/> controls pipeline position; without it
/// the default priority of <c>0</c> applies. Priority may be overridden per-registration
/// on the builder or via <c>IConfiguration</c> at runtime.
/// </para>
/// </remarks>
public interface IDmonMiddleware
{
    /// <summary>
    /// Returns an <see cref="IChatClient"/> that wraps <paramref name="inner"/>.
    /// The returned client may mutate the messages list before forwarding to
    /// <paramref name="inner"/> and/or mutate the response before returning it to
    /// the caller.
    /// </summary>
    /// <param name="inner">The next client in the pipeline. Must not be <c>null</c>.</param>
    /// <returns>
    /// An <see cref="IChatClient"/> whose calls are delegated to <paramref name="inner"/>,
    /// optionally with interception on the way in and/or out.
    /// </returns>
    /// <remarks>
    /// Implementations SHALL throw <see cref="ArgumentNullException"/> when
    /// <paramref name="inner"/> is <c>null</c>.
    /// </remarks>
    IChatClient Wrap(IChatClient inner);
}
