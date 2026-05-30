using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;

namespace Dmon.Core.Tests.Config;

/// <summary>
/// Proves that a top-level <c>middleware:&lt;ClassName&gt;</c> section is safely
/// readable from the layered <see cref="IConfiguration"/> without the
/// array-index collapse that affects the <c>extensions:</c> list.
///
/// Spec coverage (extension-middleware-tier spec, "Middleware configuration via named YAML sections"):
///   - "Arbitrary config fields are accessible via IConfigurationRoot" (single layer)
///   - "Name-keyed middleware section survives layered config" (user + project layers)
///   - "Priority override in config takes precedence over attribute" (priority field readable)
///   - "Absent config section uses attribute priority" (missing section returns null)
/// </summary>
public sealed class MiddlewareConfigSectionTests : IDisposable
{
    private readonly string _tempDir;

    public MiddlewareConfigSectionTests()
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

    // ── Scenario: Arbitrary config fields are accessible ────────────────────

    [Fact]
    public void GetSection_ReturnsArbitraryField_WhenPresentInSingleLayer()
    {
        // Spec scenario: "Arbitrary config fields are accessible via IConfigurationRoot"
        // GIVEN a project config with middleware:TokenLimitMiddleware:maxTokens set
        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "4096"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        // WHEN middleware reads its section via IConfiguration
        string? maxTokens = config.GetSection("middleware:TokenLimitMiddleware")["maxTokens"];

        // THEN the value is returned correctly
        Assert.Equal("4096", maxTokens);
    }

    [Fact]
    public void GetSection_ReturnsPriorityField_WhenPresentInSingleLayer()
    {
        // Spec scenario: "Priority override in config takes precedence over attribute"
        // GIVEN a middleware config section with a priority field
        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                priority: "50"
                maxTokens: "4096"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        IConfigurationSection section = config.GetSection("middleware:TokenLimitMiddleware");
        Assert.Equal("50", section["priority"]);
        Assert.Equal("4096", section["maxTokens"]);
    }

    [Fact]
    public void GetSection_ReturnsNull_WhenSectionAbsent()
    {
        // Spec scenario: "Absent config section uses attribute priority"
        // GIVEN a config with no middleware section at all
        string projectConfig = WriteYaml("project.yaml", """
            sessionStore: local
            """);

        IConfiguration config = BuildLayered(projectConfig);

        // WHEN middleware tries to read its section
        IConfigurationSection section = config.GetSection("middleware:TokenLimitMiddleware");
        string? maxTokens = section["maxTokens"];

        // THEN both key-value and section itself are absent (null values)
        Assert.Null(maxTokens);
        Assert.False(section.Exists());
    }

    [Fact]
    public void GetSection_ReturnsMultipleArbitraryFields_ForSameMiddleware()
    {
        // Middleware sections carry multiple arbitrary fields
        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "4096"
                logRequests: "true"
                cacheWindow: "60"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        IConfigurationSection section = config.GetSection("middleware:TokenLimitMiddleware");
        Assert.Equal("4096", section["maxTokens"]);
        Assert.Equal("true", section["logRequests"]);
        Assert.Equal("60", section["cacheWindow"]);
    }

    // ── Scenario: Name-keyed section survives layered config ─────────────────

    [Fact]
    public void GetSection_ProjectValueWins_WhenBothUserAndProjectLayersPresent()
    {
        // Spec scenario: "Name-keyed middleware section survives layered config"
        // GIVEN user config sets maxTokens to one value and project config overrides it
        // Layer order: global < project (last-wins, matching Program.cs precedence)
        string userConfig = WriteYaml("user.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "2048"
            """);

        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "4096"
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        // WHEN middleware reads its section from the layered config
        string? maxTokens = config.GetSection("middleware:TokenLimitMiddleware")["maxTokens"];

        // THEN the project-layer value wins (last-wins semantics) — no collapse
        Assert.Equal("4096", maxTokens);
    }

    [Fact]
    public void GetSection_UserValuePresent_WhenOnlyUserLayerSetsSection()
    {
        // Name-keyed middleware section is readable when only the user layer defines it
        string userConfig = WriteYaml("user.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "8192"
            """);

        // Project layer exists but does not define the middleware section
        string projectConfig = WriteYaml("project.yaml", """
            sessionStore: local
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        string? maxTokens = config.GetSection("middleware:TokenLimitMiddleware")["maxTokens"];

        Assert.Equal("8192", maxTokens);
    }

    [Fact]
    public void GetSection_MultipleMiddlewares_EachReadsItsOwnSection()
    {
        // Multiple middleware subsections in the same config file do not collide
        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "4096"
              LoggingMiddleware:
                logLevel: "debug"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        Assert.Equal("4096", config.GetSection("middleware:TokenLimitMiddleware")["maxTokens"]);
        Assert.Equal("debug", config.GetSection("middleware:LoggingMiddleware")["logLevel"]);
    }

    [Fact]
    public void GetSection_KeyLookup_IsCaseInsensitive()
    {
        // Spec scenario: "middleware:<ClassName> key is case-insensitive"
        // GIVEN a config with the section written under mixed-case key
        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                maxTokens: "4096"
            """);

        IConfiguration config = BuildLayered(projectConfig);

        // WHEN the section is read with an all-lowercase key variant
        string? maxTokens = config.GetSection("middleware:tokenlimitmiddleware")["maxTokens"];

        // THEN the value is still returned — key matching is case-insensitive
        Assert.Equal("4096", maxTokens);
    }

    [Fact]
    public void GetSection_PriorityOverride_SurvivesLayering()
    {
        // Priority override is readable and survives layered config correctly
        string userConfig = WriteYaml("user.yaml", """
            middleware:
              TokenLimitMiddleware:
                priority: "100"
            """);

        string projectConfig = WriteYaml("project.yaml", """
            middleware:
              TokenLimitMiddleware:
                priority: "50"
            """);

        IConfiguration config = BuildLayered(userConfig, projectConfig);

        string? priority = config.GetSection("middleware:TokenLimitMiddleware")["priority"];

        // Project-layer priority override wins
        Assert.Equal("50", priority);
    }
}
