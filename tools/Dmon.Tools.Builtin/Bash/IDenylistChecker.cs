namespace Dmon.Tools.Builtin.Bash;

public interface IDenylistChecker
{
    bool IsDenied(string command);
}
