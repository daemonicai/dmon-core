using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Blessed primitive verbs for the dmon composition surface.
/// These extension methods live in <c>Dmon.Hosting</c> (the default-imported namespace)
/// so they are available without an extra <c>using</c> directive in <c>Dmon.cs</c>.
/// </summary>
/// <remarks>
/// All verbs use the self-type generic pattern (<c>static T Verb&lt;T&gt;(this T r, …) where T : IFacet</c>)
/// so that calling a verb on the concrete builder returns the builder type (flat chaining)
/// and calling a verb on a bare facet returns the facet type (sub-agent reuse).
///
/// C# cannot partially infer type arguments, so a two-type-parameter form such as
/// <c>r.AddProvider&lt;GeminiProvider&gt;()</c> cannot bind to a method declared as
/// <c>Add&lt;T, TImpl&gt;(this T)</c> — the self-type <c>T</c> must also be supplied
/// explicitly, making the call site verbose and defeating the purpose of the fluent surface.
/// For DI-constructed (type-only) registrations use the concrete
/// <see cref="Dmon.Hosting.DmonHostBuilder"/> methods (<c>AddToolExtension&lt;T&gt;</c>,
/// <c>AddProvider&lt;T&gt;</c>, <c>AddMiddleware&lt;T&gt;</c>) or register directly
/// via <c>Services.AddSingleton&lt;IToolExtension, TImpl&gt;()</c>.
/// Bare-facet authors (sub-agent composition roots) use the instance overloads below.
/// </remarks>
public static class DmonRegistrationExtensions
{
    // ── IToolRegistration ────────────────────────────────────────────────────

    /// <summary>
    /// Registers a pre-constructed <see cref="IToolExtension"/> instance via DI-discovery.
    /// </summary>
    public static T AddToolExtension<T>(this T registration, IToolExtension extension)
        where T : IToolRegistration
    {
        registration.Services.AddSingleton<IToolExtension>(extension);
        return registration;
    }

    // ── IMiddlewareRegistration ───────────────────────────────────────────────

    /// <summary>
    /// Registers a pre-constructed <see cref="IDmonMiddleware"/> instance via DI-discovery.
    /// An optional <paramref name="priorityOverride"/> beats the <see cref="DmonMiddlewareAttribute"/> priority.
    /// </summary>
    public static T AddMiddleware<T>(this T registration, IDmonMiddleware middleware, int? priorityOverride = null)
        where T : IMiddlewareRegistration
    {
        registration.Services.AddSingleton<MiddlewareRegistration>(
            new MiddlewareRegistration(middleware, priorityOverride));
        return registration;
    }

    // ── IProviderRegistration ─────────────────────────────────────────────────

    /// <summary>
    /// Registers a pre-constructed <see cref="IProviderExtension"/> instance via DI-discovery.
    /// </summary>
    public static T AddProvider<T>(this T registration, IProviderExtension provider)
        where T : IProviderRegistration
    {
        registration.Services.AddSingleton<IProviderExtension>(provider);
        return registration;
    }

    /// <summary>
    /// Sets the active provider and model by writing to the configuration manager.
    /// The value is stored as <c>activeModel = "{provider}/{modelId}"</c>.
    /// Takes precedence over <c>config.yaml</c> but is overridden by
    /// <c>ConfigureConfiguration</c> callbacks registered after this call.
    /// </summary>
    public static T UseModel<T>(this T registration, string provider, string modelId)
        where T : IProviderRegistration
    {
        registration.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("activeModel", $"{provider}/{modelId}"),
        ]);
        return registration;
    }
}
