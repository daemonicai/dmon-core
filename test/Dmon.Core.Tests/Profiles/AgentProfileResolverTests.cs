using Dmon.Abstractions.Profiles;
using Dmon.Core.Config;
using Dmon.Core.Profiles;

namespace Dmon.Core.Tests.Profiles;

/// <summary>
/// Tests for <see cref="EffectiveProfileSetResolver"/> (merge, dedup, defaultProfile —
/// Group 7.2) and <see cref="AgentProfileResolver"/> (selection precedence — Group 7.4).
///
/// Mirror the conventions of <c>EffectiveExtensionSetResolverTests</c>:
/// hermetic temp dirs, WriteTempConfig helpers, IDisposable cleanup.
/// </summary>
public sealed class AgentProfileResolverTests : IDisposable
{
    private readonly string _tempDir;

    public AgentProfileResolverTests()
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

    private string WriteTempConfig(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private string AbsentConfigPath(string filename)
        => Path.Combine(_tempDir, filename + ".absent");

    private static EffectiveProfileSetResolver Resolver() => new();

    // ── 7.2 — union of disjoint user + project profiles ─────────────────────

    [Fact]
    public void Resolve_DisjointUserAndProject_BothProfilesPresent()
    {
        string user = WriteTempConfig("user.yaml", """
            profiles:
              helper:
                persona: "You are a helper."
                assets: false
                permissionMode: coding
            """);

        string project = WriteTempConfig("project.yaml", """
            profiles:
              builder:
                persona: "You are a builder."
                assets: true
                permissionMode: sandbox
            """);

        EffectiveProfileSet result = Resolver().Resolve(user, project);

        Assert.Equal(2, result.Profiles.Count);
        Assert.Contains(result.Profiles, p => p.Name == "helper");
        Assert.Contains(result.Profiles, p => p.Name == "builder");
    }

    // ── 7.2 — deduplicate by name (case-insensitive), project wins fields ───

    [Fact]
    public void Resolve_SharedName_AppearsOnce_ProjectFieldsWin()
    {
        string user = WriteTempConfig("user.yaml", """
            profiles:
              helper:
                persona: "User persona."
                assets: false
                permissionMode: coding
            """);

        string project = WriteTempConfig("project.yaml", """
            profiles:
              HELPER:
                persona: "Project persona."
                assets: true
                permissionMode: sandbox
            """);

        EffectiveProfileSet result = Resolver().Resolve(user, project);

        // Deduplication: only one entry.
        Assert.Single(result.Profiles);
        // Project fields win.
        ProfileConfigEntry entry = result.Profiles[0];
        Assert.Equal("Project persona.", entry.Persona);
        Assert.True(entry.Assets);
        Assert.Equal(PermissionMode.Sandbox, entry.PermissionMode);
    }

    // ── 7.2 — user entry position is kept in merge order ────────────────────

    [Fact]
    public void Resolve_SharedName_KeepsUserPositionInOrderWithProjectEntries()
    {
        string user = WriteTempConfig("user.yaml", """
            profiles:
              alpha:
                persona: "Alpha user."
                permissionMode: coding
              shared:
                persona: "Shared user."
                permissionMode: coding
            """);

        string project = WriteTempConfig("project.yaml", """
            profiles:
              shared:
                persona: "Shared project."
                permissionMode: coding
              beta:
                persona: "Beta project."
                permissionMode: coding
            """);

        EffectiveProfileSet result = Resolver().Resolve(user, project);

        // alpha(user pos 0), shared(user pos 1, project fields win), beta(project only)
        Assert.Equal(3, result.Profiles.Count);
        Assert.Equal("alpha", result.Profiles[0].Name);
        Assert.Equal("shared", result.Profiles[1].Name);
        Assert.Equal("Shared project.", result.Profiles[1].Persona);
        Assert.Equal("beta", result.Profiles[2].Name);
    }

    // ── 7.2 — defaultProfile: project wins when both declare it ─────────────

    [Fact]
    public void Resolve_DefaultProfile_ProjectWinsOverUser()
    {
        string user = WriteTempConfig("user.yaml", """
            defaultProfile: helper
            profiles:
              helper:
                persona: "User helper."
                permissionMode: coding
            """);

        string project = WriteTempConfig("project.yaml", """
            defaultProfile: builder
            profiles:
              builder:
                persona: "Project builder."
                permissionMode: coding
            """);

        EffectiveProfileSet result = Resolver().Resolve(user, project);

        Assert.Equal("builder", result.DefaultProfile);
    }

    // ── 7.2 — defaultProfile: user value used when project omits it ─────────

    [Fact]
    public void Resolve_DefaultProfile_UserValueUsedWhenProjectOmits()
    {
        string user = WriteTempConfig("user.yaml", """
            defaultProfile: helper
            profiles:
              helper:
                persona: "Helper."
                permissionMode: coding
            """);

        string project = WriteTempConfig("project.yaml", """
            profiles:
              builder:
                persona: "Builder."
                permissionMode: coding
            """);

        EffectiveProfileSet result = Resolver().Resolve(user, project);

        Assert.Equal("helper", result.DefaultProfile);
    }

    // ── 7.2 — personaFile resolved relative to the declaring config dir ─────

    [Fact]
    public async Task Resolve_PersonaFile_ReadsPersonaFromFile()
    {
        // Write the persona file alongside the config file.
        string personaFile = Path.Combine(_tempDir, "my-persona.txt");
        const string personaText = "You are a specialist agent.";
        File.WriteAllText(personaFile, personaText);

        string project = WriteTempConfig("project.yaml", $"""
            profiles:
              specialist:
                personaFile: my-persona.txt
                assets: false
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfile profile = await resolver.ResolveAsync("specialist", CancellationToken.None);

        Assert.Equal("specialist", profile.Name);
        Assert.Equal(personaText, profile.Persona);
    }

    // ── 7.2 — unknown profile name → actionable exception ───────────────────

    [Fact]
    public async Task ResolveAsync_UnknownRequestedProfile_ThrowsAgentProfileConfigException()
    {
        string project = WriteTempConfig("project.yaml", """
            profiles:
              known:
                persona: "I am known."
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfileConfigException ex = await Assert.ThrowsAsync<AgentProfileConfigException>(
            () => resolver.ResolveAsync("unknown-profile", CancellationToken.None));

        Assert.Contains("unknown-profile", ex.Message);
    }

    // ── 7.2 — sandbox + assets:false → actionable config error ──────────────

    [Fact]
    public async Task ResolveAsync_SandboxWithAssetsDisabled_ThrowsAgentProfileConfigException()
    {
        string project = WriteTempConfig("project.yaml", """
            profiles:
              bad:
                persona: "I am incoherent."
                assets: false
                permissionMode: sandbox
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfileConfigException ex = await Assert.ThrowsAsync<AgentProfileConfigException>(
            () => resolver.ResolveAsync("bad", CancellationToken.None));

        Assert.Contains("sandbox", ex.Message);
        Assert.Contains("assets", ex.Message);
    }

    // ── 7.2 — both persona + personaFile → error ────────────────────────────

    [Fact]
    public async Task ResolveAsync_BothPersonaAndPersonaFile_ThrowsAgentProfileConfigException()
    {
        string personaFile = Path.Combine(_tempDir, "persona.txt");
        File.WriteAllText(personaFile, "file persona");

        string project = WriteTempConfig("project.yaml", $"""
            profiles:
              ambiguous:
                persona: "inline persona"
                personaFile: persona.txt
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfileConfigException ex = await Assert.ThrowsAsync<AgentProfileConfigException>(
            () => resolver.ResolveAsync("ambiguous", CancellationToken.None));

        Assert.Contains("ambiguous", ex.Message);
    }

    // ── 7.2 — neither persona nor personaFile → error ───────────────────────

    [Fact]
    public async Task ResolveAsync_NeitherPersonaNorPersonaFile_ThrowsAgentProfileConfigException()
    {
        string project = WriteTempConfig("project.yaml", """
            profiles:
              empty:
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfileConfigException ex = await Assert.ThrowsAsync<AgentProfileConfigException>(
            () => resolver.ResolveAsync("empty", CancellationToken.None));

        Assert.Contains("empty", ex.Message);
    }

    // ── 7.4 — no config, no requestedProfile → built-in coding ──────────────

    [Fact]
    public async Task ResolveAsync_NoConfigNoRequest_ReturnsBuiltInCoding()
    {
        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            AbsentConfigPath("project"));

        AgentProfile profile = await resolver.ResolveAsync(null, CancellationToken.None);

        Assert.Equal("coding", profile.Name);
        Assert.Equal(BuiltInProfiles.CodingPersona, profile.Persona);
        Assert.False(profile.Assets);
        Assert.Equal(PermissionMode.Coding, profile.PermissionMode);
    }

    // ── 7.4 — requestedProfile overrides defaultProfile ─────────────────────

    [Fact]
    public async Task ResolveAsync_RequestedProfileOverridesDefault()
    {
        string project = WriteTempConfig("project.yaml", """
            defaultProfile: helper
            profiles:
              helper:
                persona: "I am helper."
                permissionMode: coding
              specialist:
                persona: "I am specialist."
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        // requestedProfile "specialist" overrides defaultProfile "helper".
        AgentProfile profile = await resolver.ResolveAsync("specialist", CancellationToken.None);

        Assert.Equal("specialist", profile.Name);
        Assert.Equal("I am specialist.", profile.Persona);
    }

    // ── 7.4 — defaultProfile selects a config profile ───────────────────────

    [Fact]
    public async Task ResolveAsync_DefaultProfileSelectedWhenNoRequest()
    {
        string project = WriteTempConfig("project.yaml", """
            defaultProfile: helper
            profiles:
              helper:
                persona: "I am helper."
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfile profile = await resolver.ResolveAsync(null, CancellationToken.None);

        Assert.Equal("helper", profile.Name);
        Assert.Equal("I am helper.", profile.Persona);
    }

    // ── 7.4 — explicit "coding" requestedProfile returns built-in ────────────

    [Fact]
    public async Task ResolveAsync_ExplicitCodingRequest_ReturnsBuiltInCodingWhenNotOverridden()
    {
        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            AbsentConfigPath("project"));

        AgentProfile profile = await resolver.ResolveAsync("coding", CancellationToken.None);

        Assert.Equal("coding", profile.Name);
        Assert.Same(BuiltInProfiles.Coding, profile);
    }

    // ── 7.4 — unknown defaultProfile name → actionable exception ────────────

    [Fact]
    public async Task ResolveAsync_UnknownDefaultProfile_ThrowsAgentProfileConfigException()
    {
        string project = WriteTempConfig("project.yaml", """
            defaultProfile: ghost
            profiles:
              known:
                persona: "I exist."
                permissionMode: coding
            """);

        AgentProfileResolver resolver = new(
            Resolver(),
            AbsentConfigPath("user"),
            project);

        AgentProfileConfigException ex = await Assert.ThrowsAsync<AgentProfileConfigException>(
            () => resolver.ResolveAsync(null, CancellationToken.None));

        Assert.Contains("ghost", ex.Message);
    }

    // ── ContainsProfile — membership check for gateway pre-spawn validation ──

    [Fact]
    public void ContainsProfile_BuiltInCoding_ReturnsTrue()
    {
        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("coding", AbsentConfigPath("user"), AbsentConfigPath("project"));

        Assert.True(result);
    }

    [Fact]
    public void ContainsProfile_BuiltInCodingCaseInsensitive_ReturnsTrue()
    {
        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("Coding", AbsentConfigPath("user"), AbsentConfigPath("project"));

        Assert.True(result);
    }

    [Fact]
    public void ContainsProfile_KnownConfigProfile_ReturnsTrue()
    {
        string user = WriteTempConfig("user.yaml", """
            profiles:
              writer:
                persona: "You are a writer."
                assets: false
                permissionMode: coding
            """);

        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("writer", user, AbsentConfigPath("project"));

        Assert.True(result);
    }

    [Fact]
    public void ContainsProfile_KnownProfileCaseInsensitive_ReturnsTrue()
    {
        string project = WriteTempConfig("project.yaml", """
            profiles:
              Reviewer:
                persona: "You are a reviewer."
                assets: false
                permissionMode: coding
            """);

        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("reviewer", AbsentConfigPath("user"), project);

        Assert.True(result);
    }

    [Fact]
    public void ContainsProfile_UnknownName_ReturnsFalse()
    {
        string user = WriteTempConfig("user.yaml", """
            profiles:
              writer:
                persona: "You are a writer."
                assets: false
                permissionMode: coding
            """);

        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("ghost", user, AbsentConfigPath("project"));

        Assert.False(result);
    }

    [Fact]
    public void ContainsProfile_EmptyConfig_UnknownName_ReturnsFalse()
    {
        EffectiveProfileSetResolver resolver = Resolver();

        bool result = resolver.ContainsProfile("anything", AbsentConfigPath("user"), AbsentConfigPath("project"));

        Assert.False(result);
    }
}
