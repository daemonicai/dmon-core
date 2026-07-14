using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Protocol.Permissions;
using Dmon.Tools.Builtin.Bash;
using Dmon.Tools.Builtin.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verb that registers the six built-in tools via DI-discovery.
/// </summary>
public static class AddBuiltinToolsExtensions
{
    /// <summary>
    /// Registers the six built-in tools (read_file, write_file, edit_file, glob, fetch, bash)
    /// as <see cref="IToolExtension"/> singletons so the DI-discovery path routes them into
    /// <see cref="Dmon.Abstractions.IToolRegistry"/> at host startup.
    /// </summary>
    public static T AddBuiltinTools<T>(this T registration)
        where T : IToolRegistration
    {
        registration.Services.AddHttpClient();

        registration.Services.AddSingleton<IToolExtension, ReadFileTool>();
        registration.Services.AddSingleton<IToolExtension, WriteFileTool>();
        registration.Services.AddSingleton<IToolExtension, EditFileTool>();
        registration.Services.AddSingleton<IToolExtension, GlobTool>();

        registration.Services.AddSingleton<IToolExtension>(sp =>
        {
            HttpClient httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("builtin");
            IPermissionSettings permissionSettings = sp.GetRequiredService<IPermissionSettings>();
            return new FetchTool(httpClient, permissionSettings);
        });

        registration.Services.AddSingleton<IToolExtension>(sp =>
        {
            IConfiguration configuration = sp.GetRequiredService<IConfiguration>();
            int timeoutSeconds = configuration.GetValue("Dmon:Tools:Bash:TimeoutSeconds", 30);
            return new BashTool(new DenylistChecker(), new BashCompositeDetector(), timeoutSeconds);
        });

        return registration;
    }
}
