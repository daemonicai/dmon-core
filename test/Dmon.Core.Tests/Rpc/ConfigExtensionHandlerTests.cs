using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Extensions.Security;
using Dmon.Core.Rpc;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

public sealed class ConfigExtensionHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectConfigPath;
    private readonly string _userConfigPath;

    public ConfigExtensionHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dmon-ceh-tests-" + Guid.NewGuid().ToString("N"));
        _projectConfigPath = Path.Combine(_tempDir, "project", ".dmon", "config.yaml");
        _userConfigPath = Path.Combine(_tempDir, "user", ".dmon", "config.yaml");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── test doubles ─────────────────────────────────────────

    private sealed class OkFetcher : IExtensionSourceFetcher
    {
        public Task<SourceFetchResult> FetchAsync(string packageId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceFetchResult
            {
                PackageId = packageId,
                Version = version,
                RepositoryUrl = "https://github.com/example/repo",
                CommitSha = "abc123",
                SourceFiles = new Dictionary<string, string> { ["Foo.cs"] = "// ok" }
            });
    }

    private sealed class FailingFetcher : IExtensionSourceFetcher
    {
        public Task<SourceFetchResult> FetchAsync(string packageId, string version, CancellationToken cancellationToken = default) =>
            throw new SourceNotAvailableException("no nuspec repository url");
    }

    private sealed class CleanAnalyser : IExtensionSecurityAnalyser
    {
        public Task<SecurityAnalysisReport> AnalyseAsync(SourceFetchResult source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecurityAnalysisReport
            {
                RiskLevel = RiskLevel.Low,
                Findings = [],
                Summary = "No issues found.",
                PackageId = source.PackageId,
                Version = source.Version
            });
    }

    // Redirects scope path resolution to temp dirs so tests never touch ~/.dmon/ or CWD/.dmon/.
    private sealed class TestableHandler(
        IEventEmitter emitter,
        IExtensionSourceFetcher fetcher,
        IExtensionSecurityAnalyser analyser,
        string projectConfigPath,
        string userConfigPath) : ConfigExtensionHandler(
            emitter, fetcher, analyser, new ExtensionsConfigReader(), NullLogger<ConfigExtensionHandler>.Instance)
    {
        protected override string? ResolveConfigPath(string scope) =>
            scope switch
            {
                "project" => projectConfigPath,
                "user" => userConfigPath,
                _ => null
            };
    }

    private ExtensionLoadCommand MakeCmd(string source, string? scope = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Source = source, Scope = scope };

    private TestableHandler MakeHandler(IEventEmitter emitter, IExtensionSourceFetcher? fetcher = null) =>
        new(emitter, fetcher ?? new OkFetcher(), new CleanAnalyser(), _projectConfigPath, _userConfigPath);

    // ─── tests ────────────────────────────────────────────────

    /// <summary>
    /// 4.4 test 1: nuget source with version → config written, round-trips through reader.
    /// </summary>
    [Fact]
    public async Task Add_NugetWithVersion_WritesEntryAndRoundTrips()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);

        Assert.True(File.Exists(_projectConfigPath), "project config was not created");
        ExtensionsConfigReader reader = new();
        IReadOnlyList<ExtensionEntry> entries = reader.Read(_projectConfigPath);
        Assert.Single(entries);
        Assert.Equal("nuget:Acme.Tools/1.2.3", entries[0].Source);

        // Success notice emitted
        SystemNoticeEvent notice = Assert.Single(emitter.Emitted.OfType<SystemNoticeEvent>());
        Assert.Contains("nuget:Acme.Tools/1.2.3", notice.Message);
        Assert.Contains("project", notice.Message);
        Assert.Contains("low", notice.Message);
    }

    /// <summary>
    /// 4.4 test 2: the OTHER scope's file is left untouched.
    /// </summary>
    [Fact]
    public async Task Add_ToProjectScope_LeavesUserScopeUntouched()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);

        Assert.True(File.Exists(_projectConfigPath));
        Assert.False(File.Exists(_userConfigPath), "user config should not be created");
    }

    /// <summary>
    /// 4.4 test 3: added entry round-trips through ExtensionsConfigReader.
    /// </summary>
    [Fact]
    public async Task Add_UserScope_RoundTripsViaReader()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Plugin/2.0.0", "user"), CancellationToken.None);

        ExtensionsConfigReader reader = new();
        IReadOnlyList<ExtensionEntry> entries = reader.Read(_userConfigPath);
        Assert.Single(entries);
        Assert.Equal("nuget:Acme.Plugin/2.0.0", entries[0].Source);

        Assert.False(File.Exists(_projectConfigPath));
    }

    /// <summary>
    /// 4.4 test 4: scope omitted/null → writes to project scope.
    /// </summary>
    [Fact]
    public async Task Add_NullScope_DefaultsToProject()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", null), CancellationToken.None);

        Assert.True(File.Exists(_projectConfigPath), "project config should be created for null scope");
        Assert.False(File.Exists(_userConfigPath));
    }

    /// <summary>
    /// 4.4 test 4 (variant): empty scope string → defaults to project.
    /// </summary>
    [Fact]
    public async Task Add_EmptyScope_DefaultsToProject()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", ""), CancellationToken.None);

        Assert.True(File.Exists(_projectConfigPath));
        Assert.False(File.Exists(_userConfigPath));
    }

    /// <summary>
    /// 4.4 test 5: fetcher throws SourceNotAvailableException → nothing written, error event emitted.
    /// </summary>
    [Fact]
    public async Task Add_FetcherThrows_HardRefuse_NothingWritten()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter, new FailingFetcher());

        await handler.LoadAsync(MakeCmd("nuget:BadPkg/1.0.0", "project"), CancellationToken.None);

        Assert.False(File.Exists(_projectConfigPath), "config should NOT be written on fetch failure");

        ExtensionErrorEvent error = Assert.Single(emitter.Emitted.OfType<ExtensionErrorEvent>());
        Assert.Equal("nuget:BadPkg/1.0.0", error.Source);
        Assert.Equal("sourceFetch", error.Phase);
        Assert.NotEmpty(error.Diagnostics);
    }

    /// <summary>
    /// 4.4 test 6: adding an already-present source is a no-op and emits a notice.
    /// </summary>
    [Fact]
    public async Task Add_AlreadyPresent_IsNoOp_EmitsNotice()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);
        string contentAfterFirst = await File.ReadAllTextAsync(_projectConfigPath);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);
        string contentAfterSecond = await File.ReadAllTextAsync(_projectConfigPath);

        Assert.Equal(contentAfterFirst, contentAfterSecond);

        // Second call must emit a notice, not a second write
        IEnumerable<SystemNoticeEvent> notices = emitter.Emitted.OfType<SystemNoticeEvent>();
        Assert.Equal(2, notices.Count()); // one success, one already-present
        Assert.Contains(notices, n => n.Message.Contains("already in"));
    }

    /// <summary>
    /// 4.4 test 7: NuGet source with no version → refused with a clear message, nothing written.
    /// </summary>
    [Fact]
    public async Task Add_NugetWithoutVersion_Refused()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools", "project"), CancellationToken.None);

        Assert.False(File.Exists(_projectConfigPath), "config should NOT be written when version is missing");

        ExtensionErrorEvent error = Assert.Single(emitter.Emitted.OfType<ExtensionErrorEvent>());
        Assert.Equal("nuget:Acme.Tools", error.Source);
        Assert.Equal("validation", error.Phase);
        // Message must tell the user how to fix it
        string diagnostic = Assert.Single(error.Diagnostics).ToString()!;
        Assert.Contains("1.2.3", diagnostic); // example version in the message
    }

    /// <summary>
    /// 4.4 test 8: behavioural check — only a config file write and a SystemNoticeEvent are
    /// observable after LoadAsync; no tool-registration or runtime-load side effects occur.
    /// The structural guarantee (no ExtensionService/IToolRegistry dependency) is enforced by
    /// ConfigExtensionHandler's constructor signature, which accepts no such parameters.
    /// </summary>
    [Fact]
    public async Task Add_DoesNotLoadIntoRunningProcess()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);

        // The only observable effect is a config file write and a SystemNoticeEvent.
        // No ExtensionService call, no tool registration — those types aren't even
        // a field of ConfigExtensionHandler (structural compile-time guarantee).
        Assert.Single(emitter.Emitted.OfType<SystemNoticeEvent>());
        Assert.Empty(emitter.Emitted.OfType<ExtensionErrorEvent>());

        // Config was written but contains only the source declaration — no runtime state.
        string content = await File.ReadAllTextAsync(_projectConfigPath);
        Assert.Contains("nuget:Acme.Tools/1.2.3", content);
    }

    /// <summary>
    /// Source containing YAML metacharacters (double-quote, newline) is rejected before any
    /// file write: no config written, ExtensionErrorEvent with phase "validation" emitted.
    /// </summary>
    [Theory]
    [InlineData("nuget:Bad\"Pkg/1.0.0")]
    [InlineData("nuget:Bad\nPkg/1.0.0")]
    [InlineData("nuget:Bad\rPkg/1.0.0")]
    public async Task Add_SourceWithYamlMetacharacters_Refused(string badSource)
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd(badSource, "project"), CancellationToken.None);

        Assert.False(File.Exists(_projectConfigPath), "config must NOT be written for invalid source");
        Assert.False(File.Exists(_userConfigPath));

        ExtensionErrorEvent error = Assert.Single(emitter.Emitted.OfType<ExtensionErrorEvent>());
        Assert.Equal("validation", error.Phase);
        string diagnostic = Assert.Single(error.Diagnostics).ToString()!;
        Assert.Contains("Invalid extension source", diagnostic);
    }

    /// <summary>
    /// Unknown scope → ExtensionErrorEvent with phase "validation", nothing written.
    /// </summary>
    [Fact]
    public async Task Add_UnknownScope_EmitsError()
    {
        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "global"), CancellationToken.None);

        Assert.False(File.Exists(_projectConfigPath));
        Assert.False(File.Exists(_userConfigPath));

        ExtensionErrorEvent error = Assert.Single(emitter.Emitted.OfType<ExtensionErrorEvent>());
        Assert.Equal("validation", error.Phase);
    }

    /// <summary>
    /// Local script source (no version required): written directly without fetch gate.
    /// </summary>
    [Fact]
    public async Task Add_ScriptSource_WrittenWithoutFetchGate()
    {
        FakeEventEmitter emitter = new();
        // FailingFetcher should NOT be called for script sources.
        TestableHandler handler = MakeHandler(emitter, new FailingFetcher());

        await handler.LoadAsync(MakeCmd("./tools/my-tool.csx", "project"), CancellationToken.None);

        Assert.True(File.Exists(_projectConfigPath), "project config should be created for script source");
        ExtensionsConfigReader reader = new();
        IReadOnlyList<ExtensionEntry> entries = reader.Read(_projectConfigPath);
        Assert.Single(entries);
        Assert.Equal("./tools/my-tool.csx", entries[0].Source);

        SystemNoticeEvent notice = Assert.Single(emitter.Emitted.OfType<SystemNoticeEvent>());
        Assert.Contains("n/a", notice.Message); // no risk level for local sources
    }

    /// <summary>
    /// Existing config with active extensions: key → new entry spliced directly under it.
    /// </summary>
    [Fact]
    public async Task Add_ExistingExtensionsKey_SplicesNewEntry()
    {
        const string existing =
            "sessionStore: local\n" +
            "\n" +
            "extensions:\n" +
            "  - source: \"nuget:First.Ext/1.0.0\"\n";

        Directory.CreateDirectory(Path.GetDirectoryName(_projectConfigPath)!);
        await File.WriteAllTextAsync(_projectConfigPath, existing);

        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Second.Ext/2.0.0", "project"), CancellationToken.None);

        ExtensionsConfigReader reader = new();
        IReadOnlyList<ExtensionEntry> entries = reader.Read(_projectConfigPath);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Source == "nuget:First.Ext/1.0.0");
        Assert.Contains(entries, e => e.Source == "nuget:Second.Ext/2.0.0");
    }

    /// <summary>
    /// Existing config with no extensions key (only commented template) → block appended at end.
    /// </summary>
    [Fact]
    public async Task Add_NoExtensionsKey_AppendsBlock()
    {
        const string existing =
            "# dmon configuration\n" +
            "sessionStore: local\n" +
            "\n" +
            "# extensions:\n" +
            "#   - source: example\n";

        Directory.CreateDirectory(Path.GetDirectoryName(_projectConfigPath)!);
        await File.WriteAllTextAsync(_projectConfigPath, existing);

        FakeEventEmitter emitter = new();
        TestableHandler handler = MakeHandler(emitter);

        await handler.LoadAsync(MakeCmd("nuget:Acme.Tools/1.2.3", "project"), CancellationToken.None);

        ExtensionsConfigReader reader = new();
        IReadOnlyList<ExtensionEntry> entries = reader.Read(_projectConfigPath);
        Assert.Single(entries);
        Assert.Equal("nuget:Acme.Tools/1.2.3", entries[0].Source);
    }
}
