namespace Dmon.Protocol.Permissions;

public sealed record TierSettings
{
    public IReadOnlyList<string> Allow { get; init; } = [];
    public IReadOnlyList<string> Deny { get; init; } = [];
}

public sealed record PermissionSettings
{
    public TierSettings Read { get; init; } = new();
    public TierSettings Write { get; init; } = new();
    public TierSettings Bash { get; init; } = new();
    public TierSettings Http { get; init; } = new();
}

public interface IPermissionSettings
{
    PermissionSettings Settings { get; }
    Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default);
}
