using Dmon.Core.Config;

namespace Dmon.Core.Tests.Config;

/// <summary>
/// Tests for AgentConfigResolver.
///
/// AgentConfigResolver reads from Directory.GetCurrentDirectory() which is
/// process-wide state. All tests in this class are serialised via
/// [Collection("FileSystemCwd")] to prevent parallel runs from corrupting
/// each other's working directory.
/// </summary>
[Collection("FileSystemCwd")]
public sealed class AgentConfigResolverTests : IDisposable
{
    // Use the solution root as the stable restore target — guaranteed to exist.
    private static readonly string StableRestoreDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly string _tempDir;

    public AgentConfigResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(StableRestoreDir);
        Directory.Delete(_tempDir, recursive: true);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static AgentConfigResolver MakeResolver() => new();

    private void WriteProjectFile(string filename, string content)
        => File.WriteAllText(Path.Combine(_tempDir, filename), content);

    // ── 5.2 — AGENTS.md only ────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_ProjectAgentsMd_ReturnsThatContent()
    {
        WriteProjectFile("AGENTS.md", "# Project rules\n\nDo things.\n");

        AgentConfigResult result = await MakeResolver().ResolveAsync(CancellationToken.None);

        Assert.NotNull(result.Text);
        Assert.Contains("Project rules", result.Text);
        Assert.False(result.ClaudeMdUsed);
    }

    // ── 5.2 — CLAUDE.md fallback ────────────────────────────────────────────

    [Fact]
    public async Task Resolve_ClaudeMdFallback_WhenNoAgentsMd_ReturnsContent()
    {
        WriteProjectFile("CLAUDE.md", "# Claude instructions\n\nBe helpful.\n");

        AgentConfigResult result = await MakeResolver().ResolveAsync(CancellationToken.None);

        Assert.NotNull(result.Text);
        Assert.Contains("Claude instructions", result.Text);
        Assert.True(result.ClaudeMdUsed);
    }

    // ── 5.2 — AGENTS.md takes precedence over CLAUDE.md ────────────────────

    [Fact]
    public async Task Resolve_AgentsMdPresent_ClaudeMdIgnored()
    {
        WriteProjectFile("AGENTS.md", "# Agents config\n");
        WriteProjectFile("CLAUDE.md", "# Claude config\n");

        AgentConfigResult result = await MakeResolver().ResolveAsync(CancellationToken.None);

        Assert.NotNull(result.Text);
        Assert.Contains("Agents config", result.Text);
        Assert.False(result.ClaudeMdUsed);
    }

    // ── 5.2 — no config files ───────────────────────────────────────────────

    [Fact]
    public async Task Resolve_NoConfigFiles_ReturnsEmpty()
    {
        // Temp dir is empty — no AGENTS.md, no CLAUDE.md.
        // The user-home ~/.dmon/AGENTS.md path is outside our control so we
        // only assert ClaudeMdUsed is false (no CLAUDE.md was consumed).
        AgentConfigResult result = await MakeResolver().ResolveAsync(CancellationToken.None);

        Assert.False(result.ClaudeMdUsed);
    }
}
