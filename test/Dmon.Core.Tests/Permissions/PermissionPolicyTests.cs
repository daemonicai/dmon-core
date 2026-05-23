using Dmon.Core.Permissions;

namespace Dmon.Core.Tests.Permissions;

public sealed class PermissionPolicyTests
{
    private const string Cwd = "/home/user/project";

    // --- Helpers ---

    private static PermissionPolicy BuildPolicy(
        PermissionSettings? project = null,
        PermissionSettings? global = null,
        string? workingDirectory = null)
    {
        PermissionSettings projectSettings = project ?? new PermissionSettings();
        PermissionSettings globalSettings = global ?? new PermissionSettings();

        StubSettings projectStub = new(projectSettings);
        StubSettings globalStub = new(globalSettings);

        return new PermissionPolicy(
            projectStub,
            global is not null ? globalStub : null,
            workingDirectory ?? Cwd,
            new BashCompositeDetector(),
            new DenylistChecker());
    }

    // --- Read ---

    [Fact]
    public void Read_InsideCwd_IsAllowed()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Allow, policy.EvaluateRead(Cwd + "/src/foo.cs"));
    }

    [Fact]
    public void Read_ExactlyCwd_IsAllowed()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Allow, policy.EvaluateRead(Cwd));
    }

    [Fact]
    public void Read_OutsideCwd_NoGrants_IsPrompt()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateRead("/home/user/other"));
    }

    [Fact]
    public void Read_ProjectAllowedPath_IsAllowed()
    {
        PermissionSettings project = new()
        {
            Read = new TierSettings { Allow = ["/home/user/shared"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateRead("/home/user/shared/file.txt"));
    }

    [Fact]
    public void Read_GlobalAllowedPath_IsAllowed()
    {
        PermissionSettings global = new()
        {
            Read = new TierSettings { Allow = ["/home/user/global-shared"] }
        };
        PermissionPolicy policy = BuildPolicy(global: global);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateRead("/home/user/global-shared/file.txt"));
    }

    // --- Write ---

    [Fact]
    public void Write_NoGrants_IsPrompt()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateWrite("/tmp/output.txt"));
    }

    [Fact]
    public void Write_AllowedPath_IsAllowed()
    {
        PermissionSettings project = new()
        {
            Write = new TierSettings { Allow = ["/tmp"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateWrite("/tmp/output.txt"));
    }

    [Fact]
    public void Write_DeniedSubpath_IsDenied()
    {
        PermissionSettings project = new()
        {
            Write = new TierSettings
            {
                Allow = ["/tmp"],
                Deny = ["/tmp/secrets"]
            }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        // /tmp/secrets/key.txt — deny prefix is longer → Deny wins
        Assert.Equal(PermissionResult.Deny, policy.EvaluateWrite("/tmp/secrets/key.txt"));
    }

    [Fact]
    public void Write_AllowedSubpathUnderDenied_AllowWins()
    {
        // /tmp/secrets/public is more specific than /tmp/secrets deny — Allow wins
        PermissionSettings project = new()
        {
            Write = new TierSettings
            {
                Allow = ["/tmp/secrets/public"],
                Deny = ["/tmp/secrets"]
            }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateWrite("/tmp/secrets/public/readme.txt"));
    }

    // --- Bash ---

    [Fact]
    public void Bash_Composite_IsPrompt()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateBash("ls | grep foo"));
    }

    [Fact]
    public void Bash_Composite_IsPromptEvenIfAllowed()
    {
        PermissionSettings project = new()
        {
            Bash = new TierSettings { Allow = ["ls | grep*"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        // Composite always prompts — stored approvals don't apply
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateBash("ls | grep foo"));
    }

    [Fact]
    public void Bash_Denylist_IsDeny()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Deny, policy.EvaluateBash("sudo rm -rf /"));
    }

    [Fact]
    public void Bash_Denylist_IsDenyEvenIfAllowed()
    {
        PermissionSettings project = new()
        {
            Bash = new TierSettings { Allow = ["sudo*"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Deny, policy.EvaluateBash("sudo rm -rf /"));
    }

    [Fact]
    public void Bash_ProjectDenyBeatsAllow()
    {
        PermissionSettings project = new()
        {
            Bash = new TierSettings
            {
                Allow = ["git *"],
                Deny = ["git push --force*"]
            }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Deny, policy.EvaluateBash("git push --force origin main"));
    }

    [Fact]
    public void Bash_GlobAllow_IsAllowed()
    {
        PermissionSettings project = new()
        {
            Bash = new TierSettings { Allow = ["git *"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateBash("git commit -m message"));
    }

    [Fact]
    public void Bash_GlobalAllow_IsAllowed()
    {
        PermissionSettings global = new()
        {
            Bash = new TierSettings { Allow = ["dotnet *"] }
        };
        PermissionPolicy policy = BuildPolicy(global: global);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateBash("dotnet build"));
    }

    [Fact]
    public void Bash_ProjectDenyBeatsGlobalAllow()
    {
        PermissionSettings project = new()
        {
            Bash = new TierSettings { Deny = ["git push*"] }
        };
        PermissionSettings global = new()
        {
            Bash = new TierSettings { Allow = ["git *"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project, global: global);
        // Project deny fires before global allow
        Assert.Equal(PermissionResult.Deny, policy.EvaluateBash("git push origin main"));
    }

    [Fact]
    public void Bash_NoMatch_IsPrompt()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateBash("some-unknown-tool --flag"));
    }

    // --- HTTP ---

    [Fact]
    public void Http_ExactDomainMatch_IsAllowed()
    {
        PermissionSettings project = new()
        {
            Http = new TierSettings { Allow = ["api.github.com"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Allow, policy.EvaluateHttp("api.github.com"));
    }

    [Fact]
    public void Http_SubdomainDoesNotMatchParent()
    {
        PermissionSettings project = new()
        {
            Http = new TierSettings { Allow = ["github.com"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        // api.github.com ≠ github.com per ADR-006
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateHttp("api.github.com"));
    }

    [Fact]
    public void Http_ParentDoesNotMatchSubdomain()
    {
        PermissionSettings project = new()
        {
            Http = new TierSettings { Allow = ["api.github.com"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project);
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateHttp("github.com"));
    }

    [Fact]
    public void Http_NoGrant_IsPrompt()
    {
        PermissionPolicy policy = BuildPolicy();
        Assert.Equal(PermissionResult.Prompt, policy.EvaluateHttp("example.com"));
    }

    // --- Precedence: project beats global ---

    [Fact]
    public void ProjectBeatsGlobal_ForWrite()
    {
        // Project denies /shared/secrets; global allows it.
        PermissionSettings project = new()
        {
            Write = new TierSettings { Deny = ["/shared/secrets"] }
        };
        PermissionSettings global = new()
        {
            Write = new TierSettings { Allow = ["/shared"] }
        };
        PermissionPolicy policy = BuildPolicy(project: project, global: global);
        // Project scope applies first — the deny in project wins over global allow.
        Assert.Equal(PermissionResult.Deny, policy.EvaluateWrite("/shared/secrets/key.txt"));
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
