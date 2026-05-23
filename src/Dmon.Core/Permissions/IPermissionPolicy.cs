namespace Dmon.Core.Permissions;

public enum PermissionResult { Allow, Prompt, Deny }

public interface IPermissionPolicy
{
    PermissionResult EvaluateRead(string path);
    PermissionResult EvaluateWrite(string path);
    PermissionResult EvaluateBash(string command);
    PermissionResult EvaluateHttp(string domain);
}
