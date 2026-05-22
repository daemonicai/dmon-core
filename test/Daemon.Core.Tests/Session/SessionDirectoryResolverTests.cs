using System.IO;
using Daemon.Core.Session;
using Xunit;

namespace Daemon.Core.Tests.Session;

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

    [Fact]
    public void Resolve_NoDaemonDir_ReturnsFallbackGlobalPath()
    {
        SessionDirectoryResolver resolver = new();

        string result = resolver.Resolve(_tempRoot);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".daemon",
            "sessions");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_DaemonDirNoConfig_ReturnsLocalSessions()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string daemonDir = Path.Combine(projectDir, ".daemon");
        Directory.CreateDirectory(daemonDir);

        // config.yaml exists but no sessionStore key
        File.WriteAllText(Path.Combine(daemonDir, "config.yaml"), "# empty config\n");

        SessionDirectoryResolver resolver = new();

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(projectDir, ".daemon", "sessions");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_SessionStoreLocal_ReturnsLocalSessions()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string daemonDir = Path.Combine(projectDir, ".daemon");
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(Path.Combine(daemonDir, "config.yaml"), "sessionStore: local\n");

        SessionDirectoryResolver resolver = new();

        string result = resolver.Resolve(projectDir);

        Assert.Equal(Path.Combine(projectDir, ".daemon", "sessions"), result);
    }

    [Fact]
    public void Resolve_SessionStoreGlobal_ReturnsGlobalPath()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string daemonDir = Path.Combine(projectDir, ".daemon");
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(Path.Combine(daemonDir, "config.yaml"), "sessionStore: global\n");

        SessionDirectoryResolver resolver = new();

        string result = resolver.Resolve(projectDir);

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".daemon",
            "sessions");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_SessionStoreCustomAbsolutePath_ReturnsCustomPath()
    {
        string customPath = Path.Combine(_tempRoot, "custom-sessions");

        string projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(projectDir);

        string daemonDir = Path.Combine(projectDir, ".daemon");
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(Path.Combine(daemonDir, "config.yaml"), $"sessionStore: {customPath}\n");

        SessionDirectoryResolver resolver = new();

        string result = resolver.Resolve(projectDir);

        Assert.Equal(customPath, result);
    }

    [Fact]
    public void Resolve_WalksUpFromSubdirectory()
    {
        string projectDir = Path.Combine(_tempRoot, "project");
        string deepDir = Path.Combine(projectDir, "src", "sub");
        Directory.CreateDirectory(deepDir);

        string daemonDir = Path.Combine(projectDir, ".daemon");
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(Path.Combine(daemonDir, "config.yaml"), "sessionStore: local\n");

        SessionDirectoryResolver resolver = new();

        // Resolve from a subdirectory — should walk up and find .daemon in projectDir.
        string result = resolver.Resolve(deepDir);

        Assert.Equal(Path.Combine(projectDir, ".daemon", "sessions"), result);
    }
}
