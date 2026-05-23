namespace Dmon.BuiltinTools.Bash;

public interface IDenylistChecker
{
    bool IsDenied(string command);
}
