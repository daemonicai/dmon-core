using Dmon.Protocol.Permissions;

namespace Dmon.Core.Permissions;

public interface IPermissionPolicy
{
    IPermissionSettings ProjectSettings { get; }
    IPermissionSettings? GlobalSettings { get; }
}
