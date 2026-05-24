using System.ComponentModel;
using Dmon.Core.Extensions.Security;
using Dmon.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.BuiltinTools;

internal sealed class ExtensionLoadTool : IDmonExtension
{
    private readonly IExtensionSourceFetcher _sourceFetcher;
    private readonly IExtensionSecurityAnalyser _securityAnalyser;
    private readonly AIFunction _function;

    public ExtensionLoadTool(IExtensionSourceFetcher sourceFetcher, IExtensionSecurityAnalyser securityAnalyser)
    {
        _sourceFetcher = sourceFetcher;
        _securityAnalyser = securityAnalyser;
        _function = AIFunctionFactory.Create(
            AnalyseAsync,
            "extension.analyze",
            "Fetch and analyse the source code of a dmon extension before loading it. Returns a security analysis report. If the report is acceptable, use the extension.load RPC command to install the extension.");
    }

    public string Name => "Extension Analyze Tool";
    public string Description => "Fetch and security-analyse a dmon extension before loading it.";
    public IEnumerable<AIFunction> Tools => [_function];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow; // analysis is read-only; the actual load has its own permission gate

    private async Task<string> AnalyseAsync(
        [Description("The NuGet package ID of the extension to analyse.")]
        string packageId,
        [Description("The exact version of the extension to analyse. Required — obtain from extension.search results.")]
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return "Error: packageId is required.";

        if (string.IsNullOrWhiteSpace(version))
            return "Error: version is required. Use extension.search to find the correct version before calling extension.analyze.";

        SourceFetchResult source;
        try
        {
            source = await _sourceFetcher.FetchAsync(packageId, version, cancellationToken);
        }
        catch (SourceNotAvailableException ex)
        {
            return $"Source not available: {ex.Message}\n\nThis extension cannot be loaded because its source code cannot be verified.";
        }

        SecurityAnalysisReport report = await _securityAnalyser.AnalyseAsync(source, cancellationToken);

        return SecurityReportFormatter.Format(report);
    }
}
