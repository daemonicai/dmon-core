namespace Dmon.Core.Permissions;

public interface IDenylistChecker
{
    bool IsDenied(string command);
}
