namespace Dmon.Hosting;

/// <summary>
/// Entry point for the dmon composition root. Call <see cref="CreateBuilder(string[])"/>
/// to obtain a builder, configure it, and call <c>.Build().RunAsync(ct)</c> to run
/// the JSONL/stdio core loop.
/// </summary>
public static class DmonHost
{
    /// <summary>
    /// Creates a new <see cref="DmonHostBuilder"/> that configures the provider/model,
    /// extensions, permission mode, and profile, and whose
    /// <c>.Build().RunAsync(cancellationToken)</c> runs the JSONL/stdio core loop.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the host application builder.</param>
    public static DmonHostBuilder CreateBuilder(string[] args) => new(args);
}
