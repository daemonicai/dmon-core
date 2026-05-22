using Daemon.Core.Permissions;

namespace Daemon.Core.Tests.Permissions;

public sealed class PermissionSettingsLoaderTests
{
    [Fact]
    public void Parse_EmptyFile_ReturnsDefaultSettings()
    {
        PermissionSettings result = PermissionSettingsLoader.Parse([]);

        Assert.Empty(result.Read.Allow);
        Assert.Empty(result.Read.Deny);
        Assert.Empty(result.Write.Allow);
        Assert.Empty(result.Bash.Allow);
        Assert.Empty(result.Http.Allow);
    }

    [Fact]
    public void Parse_SingleReadAllow_ParsesCorrectly()
    {
        string[] lines =
        [
            "permissions:",
            "  read:",
            "    allow:",
            "      - /Users/rendle/other-project"
        ];

        PermissionSettings result = PermissionSettingsLoader.Parse(lines);

        Assert.Single(result.Read.Allow);
        Assert.Equal("/Users/rendle/other-project", result.Read.Allow[0]);
    }

    [Fact]
    public void Parse_FullSettings_ParsesAllTiers()
    {
        string[] lines =
        [
            "permissions:",
            "  read:",
            "    allow:",
            "      - /Users/rendle/other-project",
            "  write:",
            "    allow:",
            "      - /Users/rendle/other-project",
            "    deny:",
            "      - /Users/rendle/other-project/secrets",
            "  bash:",
            "    allow:",
            "      - git *",
            "      - dotnet build*",
            "    deny:",
            "      - git push --force*",
            "  http:",
            "    allow:",
            "      - api.github.com",
            "      - registry.npmjs.org"
        ];

        PermissionSettings result = PermissionSettingsLoader.Parse(lines);

        Assert.Single(result.Read.Allow);
        Assert.Equal("/Users/rendle/other-project", result.Read.Allow[0]);

        Assert.Single(result.Write.Allow);
        Assert.Single(result.Write.Deny);
        Assert.Equal("/Users/rendle/other-project/secrets", result.Write.Deny[0]);

        Assert.Equal(2, result.Bash.Allow.Count);
        Assert.Contains("git *", result.Bash.Allow);
        Assert.Contains("dotnet build*", result.Bash.Allow);
        Assert.Single(result.Bash.Deny);
        Assert.Equal("git push --force*", result.Bash.Deny[0]);

        Assert.Equal(2, result.Http.Allow.Count);
        Assert.Contains("api.github.com", result.Http.Allow);
        Assert.Contains("registry.npmjs.org", result.Http.Allow);
    }

    [Fact]
    public void Parse_CommentsAreIgnored()
    {
        string[] lines =
        [
            "# top-level comment",
            "permissions:",
            "  read: # inline comment",
            "    allow:",
            "      - /home/user # trailing comment"
        ];

        PermissionSettings result = PermissionSettingsLoader.Parse(lines);

        Assert.Single(result.Read.Allow);
        // The comment after the value is stripped
        Assert.Equal("/home/user", result.Read.Allow[0].Trim());
    }

    [Fact]
    public void Serialise_RoundTrip_PreservesValues()
    {
        PermissionSettings original = new()
        {
            Read = new TierSettings { Allow = ["/Users/rendle/project"] },
            Write = new TierSettings
            {
                Allow = ["/Users/rendle/project"],
                Deny = ["/Users/rendle/project/secrets"]
            },
            Bash = new TierSettings
            {
                Allow = ["git *", "dotnet build*"],
                Deny = ["git push --force*"]
            },
            Http = new TierSettings { Allow = ["api.github.com"] }
        };

        string yaml = PermissionSettingsLoader.Serialise(original);
        string[] lines = yaml.Split('\n');
        PermissionSettings roundTripped = PermissionSettingsLoader.Parse(lines);

        Assert.Equal(original.Read.Allow, roundTripped.Read.Allow);
        Assert.Equal(original.Write.Allow, roundTripped.Write.Allow);
        Assert.Equal(original.Write.Deny, roundTripped.Write.Deny);
        Assert.Equal(original.Bash.Allow, roundTripped.Bash.Allow);
        Assert.Equal(original.Bash.Deny, roundTripped.Bash.Deny);
        Assert.Equal(original.Http.Allow, roundTripped.Http.Allow);
    }


    [Fact]
    public void Serialise_RoundTrip_PreservesValuesContainingHash()
    {
        PermissionSettings original = new()
        {
            Bash = new TierSettings
            {
                Allow = ["echo #literal", "grep '#start' *.md"]
            }
        };

        string yaml = PermissionSettingsLoader.Serialise(original);
        string[] lines = yaml.Split('\n');
        PermissionSettings roundTripped = PermissionSettingsLoader.Parse(lines);

        Assert.Equal(original.Bash.Allow, roundTripped.Bash.Allow);
    }

    [Fact]
    public async Task SaveAsync_WritesAndReloadsCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            PermissionSettingsLoader loader = PermissionSettingsLoader.LoadProject(tempDir);

            PermissionSettings updated = new()
            {
                Bash = new TierSettings { Allow = ["git *", "dotnet *"] }
            };

            await loader.SaveAsync(updated);

            // Load fresh from disk and verify.
            PermissionSettingsLoader reloaded = PermissionSettingsLoader.LoadProject(tempDir);
            Assert.Equal(2, reloaded.Settings.Bash.Allow.Count);
            Assert.Contains("git *", reloaded.Settings.Bash.Allow);
            Assert.Contains("dotnet *", reloaded.Settings.Bash.Allow);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
