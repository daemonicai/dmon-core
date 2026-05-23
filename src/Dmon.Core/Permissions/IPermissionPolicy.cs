using Dmon.Protocol.Enums;

namespace Dmon.Core.Permissions;

public interface IPermissionPolicy
{
    PermissionResult EvaluateRead(string path);
    PermissionResult EvaluateWrite(string path);
    PermissionResult EvaluateBash(string command);
    PermissionResult EvaluateHttp(string domain);
}
