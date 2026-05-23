using System.Text;

namespace Dmon.Core.Extensions;

/// <summary>
/// Scaffolds a NuGet-packagable extension project from a .csx script.
/// Per ADR-002: wraps the script's AIFunction instantiation in an
/// <see cref="IDmonExtension"/> class, generates a .csproj, and extracts
/// <c>#r "nuget:..."</c> directives to <c>&lt;PackageReference&gt;</c> elements.
/// </summary>
public sealed class PromoteService
{
    /// <summary>
    /// Promotes a .csx script to a NuGet extension project.
    /// Reads the script, extracts package references, and generates
    /// the extension class + .csproj files.
    /// </summary>
    /// <param name="scriptPath">Path to the .csx script file.</param>
    /// <param name="outputDirectory">
    /// Directory where the extension project will be created.
    /// Created if it does not exist.
    /// </param>
    /// <param name="extensionClassName">
    /// Name for the generated <see cref="IDmonExtension"/> class.
    /// Defaults to the script filename with "Extension" appended.
    /// </param>
    /// <returns>The generated files as a dictionary of relative-path → content.</returns>
    public async Task<PromoteResult> PromoteAsync(
        string scriptPath,
        string outputDirectory,
        string? extensionClassName = null,
        CancellationToken cancellationToken = default)
    {
        string fullScriptPath = Path.GetFullPath(scriptPath);

        if (!File.Exists(fullScriptPath))
        {
            throw new FileNotFoundException($"Script file not found: {fullScriptPath}");
        }

        string scriptText = await File.ReadAllTextAsync(fullScriptPath, cancellationToken);
        string scriptFileName = Path.GetFileNameWithoutExtension(fullScriptPath);
        string className = extensionClassName ?? $"{scriptFileName}Extension";
        string safeClassName = SanitiseIdentifier(className);

        // Extract #r directives and build the package references.
        List<ExtractedPackageReference> packageRefs = ExtractPackageReferences(scriptText);
        List<ExtractedRDirective> otherRDirectives = ExtractOtherRDirectives(scriptText);
        string cleanedScriptBody = RemoveRDirectives(scriptText);

        // Build the extension class.
        string extensionCs = GenerateExtensionClass(safeClassName, scriptFileName, scriptText, cleanedScriptBody);

        // Build the .csproj.
        string csproj = GenerateCsProj(safeClassName, scriptFileName, packageRefs);

        // Write files to output directory.
        Directory.CreateDirectory(outputDirectory);

        string csPath = Path.Combine(outputDirectory, $"{safeClassName}.cs");
        string csprojPath = Path.Combine(outputDirectory, $"{safeClassName}.csproj");

        await File.WriteAllTextAsync(csPath, extensionCs, cancellationToken);
        await File.WriteAllTextAsync(csprojPath, csproj, cancellationToken);

        return new PromoteResult(
            safeClassName,
            csPath,
            csprojPath,
            packageRefs,
            otherRDirectives);
    }

    private static string GenerateExtensionClass(
        string className,
        string scriptName,
        string originalScript,
        string cleanedBody)
    {
        StringBuilder sb = new();
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine();
        sb.AppendLine("namespace PromotedExtensions;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// Extension promoted from '{scriptName}.csx'.");
        sb.AppendLine($"/// Original script body is in the Tools property.");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed class {className} : IDmonExtension");
        sb.AppendLine("{");

        // Name property.
        sb.AppendLine($"    public string Name => \"{className}\";");
        sb.AppendLine();

        // Description property.
        sb.AppendLine($"    public string Description => \"Extension promoted from '{scriptName}.csx'\";");
        sb.AppendLine();

        // Tools property — wraps the original script body.
        sb.AppendLine("    public IEnumerable<AIFunction> Tools");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine($"            // Original script: {scriptName}.csx");
        sb.AppendLine($"            // {originalScript.Length} chars, promoted {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();

        // Emit the cleaned script body with indent.
        string[] lines = cleanedBody.Split('\n');
        bool inBlockComment = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            // Skip empty lines at the beginning and preserve them when we're in body.
            if (string.IsNullOrWhiteSpace(line))
            {
                // Skip leading whitespace before the actual code starts.
                if (inBlockComment)
                {
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine();
                    inBlockComment = true;
                }

                continue;
            }

            inBlockComment = true;
            sb.Append("            // ");
            sb.AppendLine(line.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("            // TODO: Replace the script body comments above with the actual AIFunction creation code.");
        sb.AppendLine("            // The script returned AIFunction instances. Convert them to:");
        sb.AppendLine("            //   yield return AIFunctionFactory.Create(...);");
        sb.AppendLine();
        sb.AppendLine("            yield break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateCsProj(
        string className,
        string scriptName,
        List<ExtractedPackageReference> packages)
    {
        StringBuilder sb = new();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine($"    <RootNamespace>PromotedExtensions</RootNamespace>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("    <PackageReference Include=\"Microsoft.Extensions.AI\" Version=\"*\" />");
        sb.AppendLine("    <PackageReference Include=\"Dmon.Extensions\" Version=\"*\" />");
        sb.AppendLine("  </ItemGroup>");

        if (packages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <!-- Extracted from script #r directives -->");
            sb.AppendLine("  <ItemGroup>");

            foreach (ExtractedPackageReference pkg in packages)
            {
                if (pkg.Version is not null)
                {
                    sb.AppendLine($"    <PackageReference Include=\"{EscapeXml(pkg.PackageId)}\" Version=\"{EscapeXml(pkg.Version)}\" />");
                }
                else
                {
                    sb.AppendLine($"    <PackageReference Include=\"{EscapeXml(pkg.PackageId)}\" Version=\"*\" />");
                }
            }

            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");

        return sb.ToString();
    }

    internal static List<ExtractedPackageReference> ExtractPackageReferences(string scriptText)
    {
        List<ExtractedPackageReference> packages = [];
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
                packages.Add(ParsePackage(package));
            }
            else if (lineStr.StartsWith(altNugetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string package = lineStr[altNugetPrefix.Length..].TrimEnd('"').Trim();
                packages.Add(ParsePackage(package));
            }

            text = newlineIndex >= 0
                ? remainder[(newlineIndex + 1)..]
                : ReadOnlySpan<char>.Empty;
        }

        return packages;
    }

    private static List<ExtractedRDirective> ExtractOtherRDirectives(string scriptText)
    {
        List<ExtractedRDirective> directives = [];
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

            if (!lineStr.StartsWith("#r \"nuget:", StringComparison.OrdinalIgnoreCase)
                && !lineStr.StartsWith("#r \"nuget: ", StringComparison.OrdinalIgnoreCase))
            {
                directives.Add(new ExtractedRDirective(lineStr));
            }

            text = newlineIndex >= 0
                ? remainder[(newlineIndex + 1)..]
                : ReadOnlySpan<char>.Empty;
        }

        return directives;
    }

    private static string RemoveRDirectives(string scriptText)
    {
        StringBuilder sb = new(scriptText.Length);
        ReadOnlySpan<char> text = scriptText.AsSpan();
        int pos = 0;

        while (pos < text.Length)
        {
            int hashIndex = text[pos..].IndexOf("#r", StringComparison.Ordinal);

            if (hashIndex < 0)
            {
                sb.Append(text[pos..].ToString());
                break;
            }

            // Copy everything before the directive.
            sb.Append(text.Slice(pos, hashIndex).ToString());

            // Skip the directive line.
            ReadOnlySpan<char> remainder = text[(pos + hashIndex)..];
            int newlineIndex = remainder.IndexOfAny('\r', '\n');

            if (newlineIndex >= 0)
            {
                pos += hashIndex + newlineIndex + 1;

                // Handle CRLF.
                if (newlineIndex + 1 < remainder.Length && remainder[newlineIndex] == '\r' && remainder[newlineIndex + 1] == '\n')
                {
                    pos++;
                }
            }
            else
            {
                // Directive at end of file — skip to end.
                pos = text.Length;
            }
        }

        return sb.ToString();
    }

    private static ExtractedPackageReference ParsePackage(string package)
    {
        int comma = package.IndexOf(',');

        if (comma >= 0)
        {
            return new ExtractedPackageReference(
                package[..comma].Trim(),
                package[(comma + 1)..].Trim());
        }

        return new ExtractedPackageReference(package.Trim(), null);
    }

    private static string SanitiseIdentifier(string name)
    {
        StringBuilder sb = new(name.Length);

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
        }

        string result = sb.ToString();

        if (result.Length == 0)
        {
            return "Extension";
        }

        if (char.IsDigit(result[0]))
        {
            result = "_" + result;
        }

        return result;
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}

public sealed record ExtractedPackageReference(string PackageId, string? Version);

public sealed record ExtractedRDirective(string Directive);

public sealed record PromoteResult(
    string ClassName,
    string ClassFilePath,
    string CsprojFilePath,
    IReadOnlyList<ExtractedPackageReference> PackageReferences,
    IReadOnlyList<ExtractedRDirective> OtherRDirectives);