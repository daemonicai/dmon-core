using Dmon.Abstractions.Hosting;
using Dmon.Tools.Calendar;

namespace Dmon.Hosting;

/// <summary>
/// Composition verb that wires calendar tools and abilities into the dmon builder.
/// </summary>
public static class CalendarRegistrationExtensions
{
    /// <summary>
    /// Registers <see cref="CalendarExtension"/> as an <see cref="IToolExtension"/> (global
    /// pipeline) and <see cref="CalendarAbilityProvider"/> as an
    /// <see cref="Dmon.Abstractions.Extensions.IAbilityProvider"/> under the <c>personal</c>
    /// scope (ability registry only). Both read from the <c>DCAL_BASE_URL</c> and
    /// <c>DCAL_API_KEY</c> environment variables.
    /// </summary>
    /// <typeparam name="T">The builder or facet type being configured.</typeparam>
    /// <param name="registration">The tool registration surface.</param>
    /// <returns><paramref name="registration"/>, for fluent chaining.</returns>
    /// <remarks>
    /// <para>Canonical usage:</para>
    /// <code language="csharp">
    /// builder.AddCalendarAbilities();
    /// </code>
    /// <para>
    /// Calendar event bodies are lower-sensitivity than email; both tools
    /// resolve to <see cref="Dmon.Protocol.Permissions.PermissionResult.Allow"/> without prompting.
    /// </para>
    /// </remarks>
    public static T AddCalendarAbilities<T>(this T registration)
        where T : IToolRegistration
        => registration
            .AddToolExtension(new CalendarExtension())
            .AddAbilities(new CalendarAbilityProvider());
}
