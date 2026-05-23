using Dmon.BuiltinTools.Bash;
using Dmon.BuiltinTools.Tools;

namespace Dmon.Core.Extensions;

public static class BuiltinToolsRegistration
{
    public static IToolRegistry AddBuiltinTools(this IToolRegistry registry, HttpClient httpClient, int bashTimeoutSeconds = 30)
    {
        IDenylistChecker denylist = new DenylistChecker();
        IBashCompositeDetector compositeDetector = new BashCompositeDetector();

        ReadFileTool readFile = new();
        WriteFileTool writeFile = new();
        EditFileTool editFile = new();
        GlobTool glob = new();
        FetchTool fetch = new(httpClient);
        BashTool bash = new(denylist, compositeDetector, bashTimeoutSeconds);

        registry.Register(readFile.Name, readFile, readFile.Tools);
        registry.Register(writeFile.Name, writeFile, writeFile.Tools);
        registry.Register(editFile.Name, editFile, editFile.Tools);
        registry.Register(glob.Name, glob, glob.Tools);
        registry.Register(fetch.Name, fetch, fetch.Tools);
        registry.Register(bash.Name, bash, bash.Tools);

        return registry;
    }
}
