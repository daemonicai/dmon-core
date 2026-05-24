using Dmon.BuiltinTools.Bash;
using Dmon.BuiltinTools.Tools;
using Dmon.Core.BuiltinTools;
using Dmon.Core.Extensions.NuGet;
using Dmon.Core.Extensions.Security;
using Dmon.Core.GitHub;
using Dmon.Core.Providers;

namespace Dmon.Core.Extensions;

public static class BuiltinToolsRegistration
{
    public static IToolRegistry AddBuiltinTools(this IToolRegistry registry, HttpClient httpClient, IGhCliService ghCliService, IProviderRegistry providerRegistry, int bashTimeoutSeconds = 30)
    {
        IDenylistChecker denylist = new DenylistChecker();
        IBashCompositeDetector compositeDetector = new BashCompositeDetector();

        ReadFileTool readFile = new();
        WriteFileTool writeFile = new();
        EditFileTool editFile = new();
        GlobTool glob = new();
        FetchTool fetch = new(httpClient);
        BashTool bash = new(denylist, compositeDetector, bashTimeoutSeconds);
        NuGetSearchService nugetSearch = new(httpClient, ghCliService);
        ExtensionSearchTool extensionSearch = new(nugetSearch);
        ExtensionReadmeTool extensionReadme = new(ghCliService, nugetSearch);
        ExtensionSourceFetcher sourceFetcher = new(httpClient, ghCliService);
        ExtensionSecurityAnalyser securityAnalyser = new(providerRegistry);
        ExtensionLoadTool extensionLoad = new(sourceFetcher, securityAnalyser);

        registry.Register(readFile.Name, readFile, readFile.Tools);
        registry.Register(writeFile.Name, writeFile, writeFile.Tools);
        registry.Register(editFile.Name, editFile, editFile.Tools);
        registry.Register(glob.Name, glob, glob.Tools);
        registry.Register(fetch.Name, fetch, fetch.Tools);
        registry.Register(bash.Name, bash, bash.Tools);
        registry.Register(extensionSearch.Name, extensionSearch, extensionSearch.Tools);
        registry.Register(extensionReadme.Name, extensionReadme, extensionReadme.Tools);
        registry.Register(extensionLoad.Name, extensionLoad, extensionLoad.Tools);

        return registry;
    }
}
