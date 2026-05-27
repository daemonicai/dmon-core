using Dmon.Core.Config;

namespace Dmon.Core.Tests.Config;

/// <summary>
/// Tests for <see cref="EffectiveExtensionSetResolver"/> and
/// <see cref="ExtensionSourceNormalizer"/>.
///
/// Pure-function tests use <see cref="EffectiveExtensionSetResolver.ComputeEffectiveSet"/>
/// directly (no I/O). The file-wrapper end-to-end cases use temp files via
/// <see cref="EffectiveExtensionSetResolver.Resolve"/>.
/// </summary>
public sealed class EffectiveExtensionSetResolverTests : IDisposable
{
    private readonly string _tempDir;

    public EffectiveExtensionSetResolverTests()
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

    private static ExtensionEntry Entry(string source, IReadOnlyDictionary<string, string?>? settings = null)
        => new(source, settings ?? new Dictionary<string, string?>());

    private static ExtensionEntry Entry(string source, string key, string? value)
        => new(source, new Dictionary<string, string?> { [key] = value });

    private string WriteTempConfig(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    // ── 2.1 / 2.3 — disjoint lists ──────────────────────────────────────────

    [Fact]
    public void ComputeEffectiveSet_DisjointLists_AllPresentInOrder()
    {
        // User entries should come first, then project entries, each in file order.
        List<ExtensionEntry> user = [Entry("nuget:Alpha"), Entry("script:a.csx")];
        List<ExtensionEntry> project = [Entry("nuget:Beta"), Entry("nuget:Gamma")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Equal(4, result.Count);
        Assert.Equal("nuget:Alpha", result[0].Source);
        Assert.Equal("script:a.csx", result[1].Source);
        Assert.Equal("nuget:Beta", result[2].Source);
        Assert.Equal("nuget:Gamma", result[3].Source);
    }

    // ── 2.2 — same source in both scopes: appears once, project settings win ─

    [Fact]
    public void ComputeEffectiveSet_SharedSource_AppearsOnceWithProjectSettings()
    {
        // Same nuget id in both — project settings must win; positioned in user slot.
        List<ExtensionEntry> user = [Entry("nuget:Shared", "timeout", "5")];
        List<ExtensionEntry> project = [Entry("nuget:Shared", "timeout", "30")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Single(result);
        Assert.Equal("nuget:Shared", result[0].Source);
        Assert.Equal("30", result[0].Settings["timeout"]);
    }

    [Fact]
    public void ComputeEffectiveSet_SharedSource_PositionedInUserSlot()
    {
        // Shared entry keeps user-file-order position (index 1), not appended at end.
        List<ExtensionEntry> user = [Entry("nuget:First"), Entry("nuget:Shared"), Entry("nuget:Last")];
        List<ExtensionEntry> project = [Entry("nuget:Shared", "key", "proj")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Equal(3, result.Count);
        Assert.Equal("nuget:First", result[0].Source);
        Assert.Equal("nuget:Shared", result[1].Source);
        Assert.Equal("proj", result[1].Settings["key"]);
        Assert.Equal("nuget:Last", result[2].Source);
    }

    // ── 2.2 — same nuget id, different inline versions → both present ────────

    [Fact]
    public void ComputeEffectiveSet_NugetDifferentVersions_BothPresent()
    {
        List<ExtensionEntry> user = [Entry("nuget:Acme.Tools/1.0.0")];
        List<ExtensionEntry> project = [Entry("nuget:Acme.Tools/2.0.0")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Equal(2, result.Count);
        Assert.Equal("nuget:Acme.Tools/1.0.0", result[0].Source);
        Assert.Equal("nuget:Acme.Tools/2.0.0", result[1].Source);
    }

    // ── 2.2 — nuget id case-insensitive dedup ───────────────────────────────

    [Fact]
    public void ComputeEffectiveSet_NugetIdDiffersOnlyByCase_TreatedAsOne()
    {
        List<ExtensionEntry> user = [Entry("nuget:acme.tools", "from", "user")];
        List<ExtensionEntry> project = [Entry("nuget:Acme.Tools", "from", "project")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Single(result);
        Assert.Equal("project", result[0].Settings["from"]);
    }

    // ── 2.2 — path normalization: trailing slash ─────────────────────────────

    [Fact]
    public void ComputeEffectiveSet_ScriptTrailingSlashDifference_TreatedAsOne()
    {
        List<ExtensionEntry> user = [Entry("extensions/my.csx", "from", "user")];
        List<ExtensionEntry> project = [Entry("extensions/my.csx", "from", "project")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Single(result);
        Assert.Equal("project", result[0].Settings["from"]);
    }

    [Fact]
    public void ComputeEffectiveSet_PathDiffersOnlyByCase_TreatedAsOne()
    {
        List<ExtensionEntry> user = [Entry("Extensions/My.csx", "from", "user")];
        List<ExtensionEntry> project = [Entry("extensions/my.csx", "from", "project")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Single(result);
        Assert.Equal("project", result[0].Settings["from"]);
    }

    [Fact]
    public void ComputeEffectiveSet_BackslashAndForwardSlash_TreatedAsOne()
    {
        // Windows-style path vs unix-style path for the same logical script.
        List<ExtensionEntry> user = [Entry("ext\\my.csx", "from", "user")];
        List<ExtensionEntry> project = [Entry("ext/my.csx", "from", "project")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Single(result);
        Assert.Equal("project", result[0].Settings["from"]);
    }

    // ── 2.1 / empty-config end-to-end ───────────────────────────────────────

    [Fact]
    public void Resolve_BothFilesEmpty_ReturnsEmptySet()
    {
        string project = WriteTempConfig("project.yaml", "");
        string user = WriteTempConfig("user.yaml", "");

        IReadOnlyList<ExtensionEntry> result = new EffectiveExtensionSetResolver().Resolve(user, project);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_BothFilesAbsent_ReturnsEmptySet()
    {
        string project = Path.Combine(_tempDir, "nonexistent-project.yaml");
        string user = Path.Combine(_tempDir, "nonexistent-user.yaml");

        IReadOnlyList<ExtensionEntry> result = new EffectiveExtensionSetResolver().Resolve(user, project);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_NoExtensionsKey_ReturnsEmptySet()
    {
        string project = WriteTempConfig("project.yaml", "agent:\n  name: dmon\n");
        string user = WriteTempConfig("user.yaml", "agent:\n  name: dmon\n");

        IReadOnlyList<ExtensionEntry> result = new EffectiveExtensionSetResolver().Resolve(user, project);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_ProjectOnlyHasExtensions_ReturnsThose()
    {
        string project = WriteTempConfig("project.yaml",
            "extensions:\n  - source: nuget:MyExt\n");
        string user = WriteTempConfig("user.yaml", "");

        IReadOnlyList<ExtensionEntry> result = new EffectiveExtensionSetResolver().Resolve(user, project);

        Assert.Single(result);
        Assert.Equal("nuget:MyExt", result[0].Source);
    }

    // ── 2.3 — ordering: mixed shared + disjoint ──────────────────────────────

    [Fact]
    public void ComputeEffectiveSet_MixedScopes_OrderIsUserThenProjectOnly()
    {
        // user:   [A, B(shared)]
        // project:[B(shared), C]
        // expected order: A, B (user slot, project settings), C
        List<ExtensionEntry> user = [Entry("nuget:A"), Entry("nuget:B", "s", "proj-B")];
        List<ExtensionEntry> project = [Entry("nuget:B", "s", "proj-B"), Entry("nuget:C")];

        IReadOnlyList<ExtensionEntry> result = EffectiveExtensionSetResolver.ComputeEffectiveSet(user, project);

        Assert.Equal(3, result.Count);
        Assert.Equal("nuget:A", result[0].Source);
        Assert.Equal("nuget:B", result[1].Source);
        Assert.Equal("nuget:C", result[2].Source);
    }

    // ── normalizer unit tests ────────────────────────────────────────────────

    [Theory]
    [InlineData("nuget:Foo", "nuget:foo@")]
    [InlineData("nuget:foo", "nuget:foo@")]
    [InlineData("nuget:Foo/1.2.3", "nuget:foo@1.2.3")]
    [InlineData("nuget:FOO/1.2.3", "nuget:foo@1.2.3")]
    public void Normalize_Nuget_ProducesExpectedKey(string source, string expected)
    {
        Assert.Equal(expected, ExtensionSourceNormalizer.Normalize(source));
    }

    [Theory]
    [InlineData("ext/my.csx", "script:ext/my.csx")]
    [InlineData("ext/my.csx/", "script:ext/my.csx")]      // trailing slash stripped
    [InlineData("Ext/My.csx", "script:ext/my.csx")]        // lowercased
    [InlineData("ext\\my.csx", "script:ext/my.csx")]       // backslash normalised
    public void Normalize_Script_ProducesExpectedKey(string source, string expected)
    {
        Assert.Equal(expected, ExtensionSourceNormalizer.Normalize(source));
    }

    [Theory]
    [InlineData("/path/to/my.dll", "assembly:/path/to/my.dll")]
    [InlineData("/Path/To/My.dll", "assembly:/path/to/my.dll")]
    [InlineData("/path/to/my.dll/", "assembly:/path/to/my.dll")]
    public void Normalize_Assembly_ProducesExpectedKey(string source, string expected)
    {
        Assert.Equal(expected, ExtensionSourceNormalizer.Normalize(source));
    }
}
