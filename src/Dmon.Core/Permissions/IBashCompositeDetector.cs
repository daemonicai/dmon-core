namespace Dmon.Core.Permissions;

public interface IBashCompositeDetector
{
    bool IsComposite(string command);
}
