using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;

namespace Dmon.Core.Tests.Config;

/// <summary>
/// Proves that a peer top-level <c>commands:&lt;name&gt;</c> section is safely
/// readable from the layered <see cref="IConfiguration"/> without the
/// array-index collapse that affects the <c>extensions:</c> list.
///
/// Spec coverage:
///   - "Extension reads its model setting" (single layer)
///   - "Name-keyed section survives layered config" (both user and project layers)
/// </summary>
public sealed class CommandsConfigSectionTests : IDisposable
{
    private readonly string _tempDir;

    public CommandsConfigSectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string WriteYaml(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private static IConfiguration BuildLayered(params string[] yamlPaths)
    {
        ConfigurationBuilder builder = new();
        foreach (string path in yamlPaths)
        {
            builder.AddYamlFile(path, optional: false, reloadOnChange: false);
        }
        return builder.Build();
    }

    // ── Scenario: Extension reads its model setting ──────────────────────────

    [Fact]
    public void GetSection_ReturnsModel_WhenPresentInSingleLayer()
    {
        // Spec scenario: "Extension reads its model setting"
        // GIVEN a project config with commands:dmon-websearch:model set
        string projectConfig = WriteYaml("project.yaml", """
            commands:
              dmon-websearch:
                model: gemini/gemini-2.5-flash
            """);

        IConfiguration config = BuildLayered(projectConfig);

        // WHEN the extension reads its section via the layered IConfiguration
        string? model = config.GetSection("commands:dmon-websearch")["model"];

        // THEN the value is returned correctly
        Assert.Equal("gemini/gemini-2.5-flash", model);
    }

    [Fact]
    public void GetSection_ReturnsArbitraryFields_WhenPresentInSingleLayer()
    {
        // Commands sections carry arbitrary fields beyond model
        string projectConfig = WriteYaml("project.yaml", """
            commands:
              dmon-websearch:
                model: gemini/gemini-2.5-flash
                timeout: "30"
                maxResults: "10"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        IConfigurationSection section = config.GetSection("commands:dmon-websearch");
        Assert.Equal("gemini/gemini-2.5-flash", section["model"]);
        Assert.Equal("30", section["timeout"]);
        Assert.Equal("10", section["maxResults"]);
    }

    // ── Scenario: Name-keyed section survives layered config ─────────────────

    [Fact]
    public void GetSection_ProjectValueWins_WhenBothUserAndProjectLayersPresent()
    {
        // Spec scenario: "Name-keyed section survives layered config"
        // GIVEN user config sets model to one value and project config overrides it
        // Layer order: global < project (last-wins, matching Program.cs precedence)
        string userConfig = WriteYaml("user.yaml", """
            commands:
              dmon-websearch:
                model: gemini/gemini-2.5-pro
            """);

        string projectConfig = WriteYaml("project.yaml", """
            commands:
              dmon-websearch:
                model: gemini/gemini-2.5-flash
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        // WHEN the extension reads its model setting from the layered config
        string? model = config.GetSection("commands:dmon-websearch")["model"];

        // THEN the project-layer value wins (last-wins semantics) — no collapse
        Assert.Equal("gemini/gemini-2.5-flash", model);
    }

    [Fact]
    public void GetSection_UserValuePresent_WhenOnlyUserLayerSetsSection()
    {
        // Name-keyed section is readable when only the user layer defines it
        string userConfig = WriteYaml("user.yaml", """
            commands:
              dmon-websearch:
                model: openai/gpt-4o
            """);

        // Project layer exists but does not define the section
        string projectConfig = WriteYaml("project.yaml", """
            sessionStore: local
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        string? model = config.GetSection("commands:dmon-websearch")["model"];

        Assert.Equal("openai/gpt-4o", model);
    }

    [Fact]
    public void GetSection_MultipleExtensions_EachReadsItsOwnSection()
    {
        // Multiple command extensions in the same config file do not collide
        string projectConfig = WriteYaml("project.yaml", """
            commands:
              dmon-websearch:
                model: gemini/gemini-2.5-flash
              dmon-codeinterpreter:
                model: anthropic/claude-sonnet-4-20250514
            """);

        IConfiguration config = BuildLayered(projectConfig);

        Assert.Equal("gemini/gemini-2.5-flash", config.GetSection("commands:dmon-websearch")["model"]);
        Assert.Equal("anthropic/claude-sonnet-4-20250514", config.GetSection("commands:dmon-codeinterpreter")["model"]);
    }

    // ── Contrast: extensions list collapses by index across layers ───────────
    // This is documentation-in-code: it shows WHY commands uses a name-keyed map
    // instead of the extensions list format, and why ExtensionsConfigReader exists.

    [Fact]
    public void ExtensionsList_CollapsesByIndex_AcrossLayeredFiles()
    {
        // The extensions list uses array syntax and collapses by index.
        // User layer has one entry; project layer has one entry at index 0.
        // The layered IConfiguration replaces index [0] — only one entry survives.
        // This is the exact problem a name-keyed map avoids.
        string userConfig = WriteYaml("user.yaml", """
            extensions:
              - source: nuget:User.Extension/1.0.0
            """);

        string projectConfig = WriteYaml("project.yaml", """
            extensions:
              - source: ./scripts/project-helper.csx
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        IConfigurationSection extensionsSection = config.GetSection("extensions");
        List<IConfigurationSection> entries = [.. extensionsSection.GetChildren()];

        // Only one entry survives (index 0 from project overwrites index 0 from user)
        Assert.Single(entries);
        Assert.Equal("./scripts/project-helper.csx", entries[0]["source"]);
        // The user extension is gone — this is the collapse ExtensionsConfigReader prevents.
    }
}
