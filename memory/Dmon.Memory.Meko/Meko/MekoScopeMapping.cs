using Dmon.Abstractions.Memory;

namespace Dmon.Memory.Meko;

/// <summary>
/// Single mapping point between <see cref="MemoryScope"/> and the Meko <c>scope</c>
/// string. Adjust the values here once Meko's accepted values are confirmed (task 5.2).
/// This is the ONLY place in the codebase that knows the string form of a scope.
/// </summary>
internal static class MekoScopeMapping
{
    // Assumed defaults per task 5.2; verify on Discord and update here only.
    private static readonly Dictionary<MemoryScope, string> Mapping = new()
    {
        [MemoryScope.Agent] = "agent",
        [MemoryScope.Session] = "session",
        [MemoryScope.User] = "user",
        [MemoryScope.Shared] = "shared",
    };

    /// <summary>
    /// Returns the Meko <c>scope</c> string for the given <paramref name="scope"/>.
    /// Falls back to <c>"agent"</c> for any unrecognized value (defensive).
    /// </summary>
    public static string ToMekoScope(MemoryScope scope) =>
        Mapping.TryGetValue(scope, out string? value) ? value : "agent";
}
