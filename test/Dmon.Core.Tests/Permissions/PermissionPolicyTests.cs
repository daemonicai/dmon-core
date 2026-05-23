using Dmon.Core.Permissions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;

namespace Dmon.Core.Tests.Permissions;

public sealed class PermissionPolicyTests
{
    // --- ProjectSettings / GlobalSettings properties ---

    [Fact]
    public void ProjectSettings_ReturnsConstructedValue()
    {
        StubSettings project = new(new PermissionSettings());
        PermissionPolicy policy = new(project, null);
        Assert.Same(project, policy.ProjectSettings);
    }

    [Fact]
    public void GlobalSettings_ReturnsNullWhenNotProvided()
    {
        StubSettings project = new(new PermissionSettings());
        PermissionPolicy policy = new(project, null);
        Assert.Null(policy.GlobalSettings);
    }

    [Fact]
    public void GlobalSettings_ReturnsConstructedValue()
    {
        StubSettings project = new(new PermissionSettings());
        StubSettings global = new(new PermissionSettings());
        PermissionPolicy policy = new(project, global);
        Assert.Same(global, policy.GlobalSettings);
    }

    // --- NormalisePath ---

    [Fact]
    public void NormalisePath_ResolvesRelativeDots()
    {
        string result = PermissionPolicy.NormalisePath("/home/user/../user/project");
        Assert.Equal("/home/user/project", result);
    }

    // --- IsUnder ---

    [Fact]
    public void IsUnder_PathInsideDirectory_IsTrue()
    {
        Assert.True(PermissionPolicy.IsUnder("/home/user/project/src/foo.cs", "/home/user/project"));
    }

    [Fact]
    public void IsUnder_ExactMatch_IsTrue()
    {
        Assert.True(PermissionPolicy.IsUnder("/home/user/project", "/home/user/project"));
    }

    [Fact]
    public void IsUnder_PathOutsideDirectory_IsFalse()
    {
        Assert.False(PermissionPolicy.IsUnder("/home/user/other", "/home/user/project"));
    }

    [Fact]
    public void IsUnder_PrefixWithoutSeparator_IsFalse()
    {
        // /home/user/projectX is NOT under /home/user/project
        Assert.False(PermissionPolicy.IsUnder("/home/user/projectX/file", "/home/user/project"));
    }

    // --- LongestPrefixMatch ---

    [Fact]
    public void LongestPrefixMatch_NoPatterns_ReturnsNegativeOne()
    {
        Assert.Equal(-1, PermissionPolicy.LongestPrefixMatch("/tmp/file", []));
    }

    [Fact]
    public void LongestPrefixMatch_MatchingPattern_ReturnsLength()
    {
        int result = PermissionPolicy.LongestPrefixMatch("/tmp/secrets/key", ["/tmp"]);
        Assert.True(result >= 0);
    }

    [Fact]
    public void LongestPrefixMatch_LongerPatternWins()
    {
        int shortScore = PermissionPolicy.LongestPrefixMatch("/tmp/secrets/key", ["/tmp"]);
        int longScore = PermissionPolicy.LongestPrefixMatch("/tmp/secrets/key", ["/tmp/secrets"]);
        Assert.True(longScore > shortScore);
    }

    // --- ResolvePathGrant ---

    [Fact]
    public void ResolvePathGrant_NoPatterns_ReturnsNull()
    {
        TierSettings tier = new();
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/tmp/file", tier, null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolvePathGrant_AllowOnly_ReturnsAllow()
    {
        TierSettings tier = new() { Allow = ["/tmp"] };
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/tmp/file", tier, null);
        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void ResolvePathGrant_DenyLongerThanAllow_ReturnsDeny()
    {
        TierSettings tier = new()
        {
            Allow = ["/tmp"],
            Deny = ["/tmp/secrets"]
        };
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/tmp/secrets/key", tier, null);
        Assert.Equal(PermissionResult.Deny, result);
    }

    [Fact]
    public void ResolvePathGrant_AllowLongerThanDeny_ReturnsAllow()
    {
        TierSettings tier = new()
        {
            Allow = ["/tmp/secrets/public"],
            Deny = ["/tmp/secrets"]
        };
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/tmp/secrets/public/readme", tier, null);
        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void ResolvePathGrant_ProjectHasNoMatch_FallsBackToGlobal()
    {
        TierSettings projectTier = new();
        TierSettings globalTier = new() { Allow = ["/shared"] };
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/shared/file", projectTier, globalTier);
        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void ResolvePathGrant_ProjectDenyBeatsGlobalAllow()
    {
        TierSettings projectTier = new() { Deny = ["/shared/secrets"] };
        TierSettings globalTier = new() { Allow = ["/shared"] };
        PermissionResult? result = PermissionPolicy.ResolvePathGrant("/shared/secrets/key", projectTier, globalTier);
        Assert.Equal(PermissionResult.Deny, result);
    }

    // --- GlobMatch ---

    [Fact]
    public void GlobMatch_ExactMatch_IsTrue()
    {
        Assert.True(PermissionPolicy.GlobMatch("git status", "git status"));
    }

    [Fact]
    public void GlobMatch_StarSuffix_MatchesAnything()
    {
        Assert.True(PermissionPolicy.GlobMatch("git commit -m message", "git *"));
    }

    [Fact]
    public void GlobMatch_StarPrefix_MatchesAnything()
    {
        Assert.True(PermissionPolicy.GlobMatch("some tool", "* tool"));
    }

    [Fact]
    public void GlobMatch_NoMatch_IsFalse()
    {
        Assert.False(PermissionPolicy.GlobMatch("dotnet build", "git *"));
    }

    [Fact]
    public void GlobMatch_TrailingStar_MatchesEmpty()
    {
        Assert.True(PermissionPolicy.GlobMatch("git", "git*"));
    }

    // --- MatchesBashGlob ---

    [Fact]
    public void MatchesBashGlob_MatchingPattern_IsTrue()
    {
        Assert.True(PermissionPolicy.MatchesBashGlob("git commit -m msg", ["git *"]));
    }

    [Fact]
    public void MatchesBashGlob_NoPatterns_IsFalse()
    {
        Assert.False(PermissionPolicy.MatchesBashGlob("git commit", []));
    }

    [Fact]
    public void MatchesBashGlob_NonMatchingPattern_IsFalse()
    {
        Assert.False(PermissionPolicy.MatchesBashGlob("dotnet build", ["git *"]));
    }

    // --- Stub ---

    private sealed class StubSettings : IPermissionSettings
    {
        public StubSettings(PermissionSettings settings) => Settings = settings;
        public PermissionSettings Settings { get; }
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
