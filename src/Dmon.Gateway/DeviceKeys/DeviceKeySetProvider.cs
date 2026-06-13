namespace Dmon.Gateway.DeviceKeys;

/// <summary>
/// Singleton mutable holder for the active <see cref="DeviceKeySet"/>.
///
/// Initialized at startup from <c>devices.json</c>; the group-4 file watcher
/// calls <see cref="Update"/> to swap in a freshly-loaded set without restarting.
///
/// A <see langword="volatile"/> field is used so the watcher thread's write is
/// immediately visible to request threads reading <see cref="Current"/>.
/// </summary>
internal sealed class DeviceKeySetProvider
{
    private volatile DeviceKeySet _current;

    public DeviceKeySetProvider(DeviceKeySet initial)
    {
        _current = initial;
    }

    /// <summary>The current active key set. Safe to read from any thread.</summary>
    public DeviceKeySet Current => _current;

    /// <summary>
    /// Replaces the current key set. Called by the group-4 file watcher on reload.
    /// </summary>
    public void Update(DeviceKeySet set)
    {
        _current = set;
    }
}
