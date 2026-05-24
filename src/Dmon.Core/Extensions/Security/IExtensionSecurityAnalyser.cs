namespace Dmon.Core.Extensions.Security;

public interface IExtensionSecurityAnalyser
{
    Task<SecurityAnalysisReport> AnalyseAsync(
        SourceFetchResult source,
        CancellationToken cancellationToken = default);
}
