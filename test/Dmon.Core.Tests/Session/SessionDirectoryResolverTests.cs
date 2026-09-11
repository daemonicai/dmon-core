using Dmon.Core.Session;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Tests.Session;

public sealed class SessionDirectoryResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public SessionDirectoryResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static IConfiguration CreateConfig(string? sessionStore = null)
    {
        ConfigurationBuilder builder = new();
        if (sessionStore is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["sessionStore"] = sessionStore
            });
        }
        return builder.Build();
    }

    [Fact]
    public void Resolve_NoDmonDir_ReturnsFallbackGlobalPath()
    {
        IConfiguration config = CreateConfig();
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(_tempRoot);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon",
            "sessions");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_DmonDirNoConfig_ReturnsLocalSessions()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);

        // config.yaml exists but no sessionStore key — default to "local"
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), "# empty config\n");

        IConfiguration config = CreateConfig();
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(projectDir, ".dmon", "sessions");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_SessionStoreLocal_ReturnsLocalSessions()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), "sessionStore: local\n");

        IConfiguration config = CreateConfig("local");
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        Assert.Equal(Path.Combine(projectDir, ".dmon", "sessions"), result);
    }

    [Fact]
    public void Resolve_SessionStoreGlobal_ReturnsGlobalPath()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), "sessionStore: global\n");

        IConfiguration config = CreateConfig("global");
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon",
            "sessions");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_SessionStoreCustomAbsolutePath_ReturnsCustomPath()
    {
        string customPath = Path.Combine(_tempRoot, "custom-sessions");

        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), $"sessionStore: {customPath}\n");

        IConfiguration config = CreateConfig(customPath);
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        Assert.Equal(customPath, result);
    }

    [Fact]
    public void Resolve_DmonDirOnlyConfigLocal_ReturnsFallbackGlobalPath()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);

        // .dmon/ holds only the app-managed config.local.yaml — no config.yaml marker.
        File.WriteAllText(Path.Combine(dmonDir, "config.local.yaml"), "activeModel: gpt-5\n");

        AssertNoAncestorConfigYaml(projectDir);

        IConfiguration config = CreateConfig();
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon",
            "sessions");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_DmonDirConfigLocalAndConfigYaml_ReturnsLocalSessions()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);

        File.WriteAllText(Path.Combine(dmonDir, "config.local.yaml"), "activeModel: gpt-5\n");
        // Adding config.yaml alongside config.local.yaml is what marks this a root.
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), "# empty config\n");

        IConfiguration config = CreateConfig();
        SessionDirectoryResolver resolver = new(config);

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(projectDir, ".dmon", "sessions");
        Assert.Equal(expected, result);
    }

    private static void AssertNoAncestorConfigYaml(string start)
    {
        string? current = Path.GetFullPath(start);

        while (current is not null)
        {
            string candidate = Path.Combine(current, ".dmon", "config.yaml");
            Assert.False(
                File.Exists(candidate),
                $"Test precondition violated: '{candidate}' exists, so this temp tree cannot discriminate a bare .dmon/ from a real root.");

            string? parent = Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent;
        }
    }

    [Fact]
    public void Resolve_WalksUpFromSubdirectory()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        string deepDir = Path.Combine(projectDir, "src", "sub");
        Directory.CreateDirectory(deepDir);

        string dmonDir = Path.Combine(projectDir, ".dmon");
        Directory.CreateDirectory(dmonDir);
        File.WriteAllText(Path.Combine(dmonDir, "config.yaml"), "sessionStore: local\n");

        IConfiguration config = CreateConfig("local");
        SessionDirectoryResolver resolver = new(config);

        // Resolve from a subdirectory — should walk up and find .dmon in projectDir.
        string result = resolver.Resolve(deepDir);

        Assert.Equal(Path.Combine(projectDir, ".dmon", "sessions"), result);
    }
}
