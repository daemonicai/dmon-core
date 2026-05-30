using Microsoft.Extensions.AI;

namespace Dmon.Extensions;

/// <summary>
/// Defines the contract for a dmon middleware extension.
/// Middleware wraps the <see cref="IChatClient"/> pipeline, intercepting every request
/// and response unconditionally — enabling capabilities such as semantic caching,
/// context-window management, RAG injection, guardrails, and observability.
/// </summary>
/// <remarks>
/// <para>
/// To be discovered by the extension loader, a class must BOTH implement
/// <see cref="IDmonMiddleware"/> AND carry <see cref="DmonMiddlewareAttribute"/>.
/// Implementing the interface without the attribute is not an error, but the class
/// will not be included in the pipeline.
/// </para>
/// <para>
/// To implement middleware, return a new <see cref="IChatClient"/> from
/// <see cref="Wrap"/> that delegates to <paramref name="inner"/> — typically by
/// subclassing <c>Microsoft.Extensions.AI.DelegatingChatClient</c>.
/// </para>
/// <para>
/// Middleware is loaded once at agent startup. Changes to middleware assemblies
/// require a process restart; hot-reload is not supported for this tier.
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
