namespace Dmon.Network.DeviceKeys;

/// <summary>
/// Result of a device-key authentication check.
/// </summary>
/// <param name="Authorized">Whether the request is authorized.</param>
/// <param name="KeyId">
/// The matched device key identifier, or <see langword="null"/> when authorized via the
/// empty-set short-circuit (auth disabled).
/// </param>
internal readonly record struct DeviceAuthResult(bool Authorized, string? KeyId)
{
    /// <summary>
    /// The request is authorized but there is no associated key (empty-set / auth-disabled case).
    /// </summary>
    public static readonly DeviceAuthResult AuthorizedNoKey = new(Authorized: true, KeyId: null);

    /// <summary>
    /// The request is not authorized.
    /// </summary>
    public static readonly DeviceAuthResult NotAuthorized = new(Authorized: false, KeyId: null);
}
