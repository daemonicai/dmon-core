using System.Collections.Immutable;

namespace Dmon.Network.DeviceKeys;

/// <summary>
/// Immutable snapshot of the active (non-revoked) device credentials.
/// This is the type swapped behind a single reference on hot reload (group 4).
///
/// An empty set is a first-class state meaning auth is disabled — the gateway
/// authorizes every connection when <see cref="IsEmpty"/> is <see langword="true"/>.
/// </summary>
internal sealed class DeviceKeySet
{
    /// <summary>
    /// Empty set — auth disabled. Returned when <c>devices.json</c> is absent.
    /// Singleton identity is only the absent-file sentinel; use <see cref="IsEmpty"/>,
    /// not reference equality, to detect the disabled state.
    /// </summary>
    public static readonly DeviceKeySet Empty = new([]);

    private readonly ImmutableArray<DeviceCredential> _entries;

    internal DeviceKeySet(ImmutableArray<DeviceCredential> activeEntries)
    {
        _entries = activeEntries;
    }

    /// <summary>
    /// <see langword="true"/> when there are no active credentials (auth disabled).
    /// </summary>
    public bool IsEmpty => _entries.IsEmpty;

    /// <summary>
    /// The active credential entries. Callers should not cache this list across reloads.
    /// </summary>
    public IReadOnlyList<DeviceCredential> Entries => _entries;
}
