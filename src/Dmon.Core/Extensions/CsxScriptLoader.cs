using Dotnet.Script.Core;
using Dotnet.Script.DependencyModel.Context;
using Dotnet.Script.DependencyModel.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Loads .csx script extensions using Dotnet.Script.Core.
/// Scripts return <see cref="AIFunction"/> instances directly;
/// they do not implement <see cref="IDmonExtension"/>.
/// </summary>
public sealed class CsxScriptLoader : IExtensionLoader
{
    private readonly LogFactory _logFactory;
    private readonly ScriptCompiler _compiler;
    private readonly ScriptConsole _scriptConsole;

    public string SourceKind => "script";

    public Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>>? ConfirmCallback { get; set; }

    public CsxScriptLoader()
    {
        _logFactory = new LogFactory(type => (Dotnet.Script.DependencyModel.Logging.LogLevel level, string message, Exception? exception) =>
        {
            if (level >= Dotnet.Script.DependencyModel.Logging.LogLevel.Warning)
            {
                System.Diagnostics.Debug.WriteLine($"[Csx {level}] {type?.Name}: {message}");
            }
        });

        _scriptConsole = new ScriptConsole(
            Console.Out,
            Console.In,
            Console.Error);

        _compiler = new ScriptCompiler(_logFactory, cachePath: null!, useRestoreCache: false);
    }

    public async Task<ExtensionLoadResult> LoadAsync(
        ParsedExtensionSource source,
        CancellationToken cancellationToken = default)
    {
        string scriptPath = source.Value;
        string fullPath = Path.GetFullPath(scriptPath);

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(
                source.Value,
                "resolve",
                $"Script file not found: {fullPath}");
        }

        string scriptText = await File.ReadAllTextAsync(fullPath, cancellationToken);

        // Check for #r "nuget:..." directives for permission gating.
        List<string> nugetDirectives = ExtractNuGetDirectives(scriptText);

        if (nugetDirectives.Count > 0 && ConfirmCallback is not null)
        {
            string packageList = string.Join(", ", nugetDirectives);
            ExtensionLoadConfirmRequest resolveRequest = new()
            {
                Source = source.Value,
                Phase = "resolve",
                Description = $"Script requires NuGet packages: {packageList}"
            };

            bool confirmed = await ConfirmCallback(resolveRequest, cancellationToken);

            if (!confirmed)
            {
                return CreateErrorResult(
                    source.Value,
                    "resolve",
                    $"Permission denied for NuGet packages: {packageList}");
            }
        }

        // Phase "load" — compile and execute the script, which may load assemblies.
        if (ConfirmCallback is not null)
        {
            ExtensionLoadConfirmRequest loadRequest = new()
            {
                Source = source.Value,
                Phase = "load",
                Description = $"Execute .csx script '{Path.GetFileName(fullPath)}'"
            };

            bool confirmed = await ConfirmCallback(loadRequest, cancellationToken);

            if (!confirmed)
            {
                return CreateErrorResult(
                    source.Value,
                    "load",
                    $"Permission denied for script '{Path.GetFileName(fullPath)}'.");
            }
        }

        SourceText code = SourceText.From(scriptText);
        string workingDir = Path.GetDirectoryName(fullPath)!;

        ScriptContext context = new(
            code,
            workingDir,
            [],
            fullPath,
            OptimizationLevel.Debug,
            ScriptMode.Script,
            []);

        try
        {
            ScriptRunner runner = new(_compiler, _logFactory, _scriptConsole);
            object? result = await runner.Execute<object>(context);

            if (result is null)
            {
                return CreateErrorResult(
                    source.Value,
                    "execute",
                    "Script returned null. Scripts must return an AIFunction or IEnumerable<AIFunction>.");
            }

            List<AIFunction> functions = ExtractFunctions(result);

            if (functions.Count == 0)
            {
                return CreateErrorResult(
                    source.Value,
                    "execute",
                    "Script did not return any AIFunction instances.");
            }

            string extName = Path.GetFileNameWithoutExtension(fullPath);

            return new ExtensionLoadResult
            {
                Name = extName,
                Description = $"Script extension from {Path.GetFileName(fullPath)}",
                Tools = functions,
                SourceKind = "script"
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResult(
                source.Value,
                "execute",
                $"Script execution failed: {ex.Message}");
        }
    }

    private static List<string> ExtractNuGetDirectives(string scriptText)
    {
        List<string> packages = [];
        ReadOnlySpan<char> text = scriptText.AsSpan();

        while (true)
        {
            int hashIndex = text.IndexOf("#r", StringComparison.Ordinal);

            if (hashIndex < 0)
            {
                break;
            }

            ReadOnlySpan<char> remainder = text[hashIndex..];
            int newlineIndex = remainder.IndexOfAny('\r', '\n');

            ReadOnlySpan<char> line = newlineIndex >= 0
                ? remainder[..newlineIndex]
                : remainder;

            string lineStr = line.ToString().Trim();

            const string nugetPrefix = "#r \"nuget:";
            const string altNugetPrefix = "#r \"nuget: ";

            if (lineStr.StartsWith(nugetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string package = lineStr[nugetPrefix.Length..].TrimEnd('"').Trim();
                packages.Add(package);
            }
            else if (lineStr.StartsWith(altNugetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string package = lineStr[altNugetPrefix.Length..].TrimEnd('"').Trim();
                packages.Add(package);
            }

            text = newlineIndex >= 0
                ? remainder[(newlineIndex + 1)..]
                : ReadOnlySpan<char>.Empty;
        }

        return packages;
    }

    private static List<AIFunction> ExtractFunctions(object result)
    {
        if (result is AIFunction single)
        {
            return [single];
        }

        if (result is IEnumerable<AIFunction> enumerable)
        {
            return enumerable.ToList();
        }

        if (result is IEnumerable<object> objectEnumerable)
        {
            return objectEnumerable.OfType<AIFunction>().ToList();
        }

        return [];
    }

    private static ExtensionLoadResult CreateErrorResult(
        string source,
        string phase,
        string message)
    {
        return new ExtensionLoadResult
        {
            Name = $"__error__{Guid.NewGuid():N}",
            Description = $"ERROR[{phase}]: {message}",
            Tools = [],
            SourceKind = source
        };
    }
}