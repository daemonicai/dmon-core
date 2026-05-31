using Dmon.Abstractions.Profiles;
using Dmon.Core.Profiles;

namespace Dmon.Core.Tests.Profiles;

/// <summary>
/// Verifies per-session asset directory provisioning behaviour for Group 6.
/// Uses a temp workspace so tests are hermetic and leave no permanent artefacts.
/// </summary>
public sealed class SessionAssetProvisionerTests : IDisposable
{
    private readonly string _workspace;

    public SessionAssetProvisionerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    // ── 6.1 — assets-enabled profile provisions the directory ───────────────

    [Fact]
    public void Provision_AssetsTrue_CreatesAssetDirectory()
    {
        SessionAssetProvisioner provisioner = new(_workspace);
        AgentProfile profile = new("agent", "", Assets: true, PermissionMode.Coding);
        const string sessionId = "sess-abc";

        string? result = provisioner.Provision(profile, sessionId);

        string expected = Path.GetFullPath(Path.Combine(_workspace, "assets", sessionId));
        Assert.Equal(expected, result);
        Assert.True(Directory.Exists(expected));
    }

    // ── 6.1 — assets-disabled profile creates no directory ──────────────────

    [Fact]
    public void Provision_AssetsFalse_CreatesNothing()
    {
        SessionAssetProvisioner provisioner = new(_workspace);
        AgentProfile profile = new("coding", "", Assets: false, PermissionMode.Coding);

        string? result = provisioner.Provision(profile, "sess-xyz");

        Assert.Null(result);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "assets")));
    }

    // ── 6.1 — null session id creates no directory ──────────────────────────

    [Fact]
    public void Provision_NullSessionId_CreatesNothing()
    {
        SessionAssetProvisioner provisioner = new(_workspace);
        AgentProfile profile = new("agent", "", Assets: true, PermissionMode.Coding);

        string? result = provisioner.Provision(profile, sessionId: null);

        Assert.Null(result);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "assets")));
    }

    // ── 6.1 — idempotent: calling twice does not throw ──────────────────────

    [Fact]
    public void Provision_CalledTwice_IsIdempotent()
    {
        SessionAssetProvisioner provisioner = new(_workspace);
        AgentProfile profile = new("agent", "", Assets: true, PermissionMode.Coding);
        const string sessionId = "sess-idem";

        string? first = provisioner.Provision(profile, sessionId);
        string? second = provisioner.Provision(profile, sessionId);

        Assert.Equal(first, second);
        Assert.True(Directory.Exists(first!));
    }

    // ── 6.2 — asset dir is distinct from session attachments/ ───────────────

    [Fact]
    public void Provision_AssetPath_IsDistinctFromAttachmentsDir()
    {
        SessionAssetProvisioner provisioner = new(_workspace);
        AgentProfile profile = new("agent", "", Assets: true, PermissionMode.Coding);
        const string sessionId = "sess-dist";

        string? assetPath = provisioner.Provision(profile, sessionId);

        // Session attachments live inside the session-storage directory, e.g.:
        //   <store>/.dmon/sessions/<id>/attachments/
        // The asset path must NOT be under a .dmon subtree.
        Assert.NotNull(assetPath);
        Assert.DoesNotContain(".dmon", assetPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("assets", sessionId), assetPath);
    }
}
