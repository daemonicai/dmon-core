using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Calendar;

/// <summary>
/// Exposes calendar tools as abilities under the <c>personal</c> scope.
/// Registered via <see cref="Dmon.Hosting.CalendarRegistrationExtensions.AddCalendarAbilities{T}"/>.
/// </summary>
/// <remarks>
/// Ability providers are consumed by <c>AbilityRegistry.ForScope</c> and never enter the
/// global tool pipeline — they are orthogonal to <see cref="CalendarExtension"/>.
/// </remarks>
public sealed class CalendarAbilityProvider : IAbilityProvider
{
    private readonly AIFunction[] _tools;

    /// <summary>
    /// Creates the provider from environment configuration: <c>DCAL_BASE_URL</c>
    /// (default <c>http://localhost:5280</c>) and <c>DCAL_API_KEY</c>.
    /// </summary>
    public CalendarAbilityProvider()
        : this(
            Environment.GetEnvironmentVariable("DCAL_BASE_URL") is { Length: > 0 } url ? url : "http://localhost:5280",
            Environment.GetEnvironmentVariable("DCAL_API_KEY"))
    {
    }

    /// <summary>Creates the provider against an explicit Daemon.Calendar endpoint.</summary>
    /// <param name="baseUrl">Base URL of the calendar server.</param>
    /// <param name="apiKey">Calendar API key; null if the server runs without auth.</param>
    /// <param name="httpClient">Optional client to reuse for requests.</param>
    public CalendarAbilityProvider(string baseUrl, string? apiKey, HttpClient? httpClient = null)
    {
        var ext = new CalendarExtension(baseUrl, apiKey, httpClient);
        _tools = ext.Tools.Cast<AIFunction>().ToArray();
    }

    /// <inheritdoc />
    public string Scope => "personal";

    /// <inheritdoc />
    public IEnumerable<AITool> Tools => _tools;
}
