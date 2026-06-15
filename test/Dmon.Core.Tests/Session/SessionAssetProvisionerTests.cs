using Dmon.Core.Session;

namespace Dmon.Core.Tests.Session;

/// <summary>
/// Verifies per-session asset directory provisioning behaviour (task 7.4 — rewired
/// to the new <see cref="ISessionAssetProvisioner"/> signature introduced in task 7.2).
///
/// The old tests lived in <c>Profiles/SessionAssetProvisionerTests.cs</c> and used the
/// deleted <c>AgentProfile</c> type. These tests cover identical semantics using the
/// new <c>bool assetsEnabled, string? workspaceRoot, string? sessionId</c> signature.
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

    // ── assets-enabled provisions the directory ──────────────────────────────

    [Fact]
    public void Provision_AssetsEnabled_CreatesAssetDirectory()
    {
        SessionAssetProvisioner provisioner = new();
        const string sessionId = "sess-abc";

        string? result = provisioner.Provision(assetsEnabled: true, _workspace, sessionId);

        string expected = Path.GetFullPath(Path.Combine(_workspace, "assets", sessionId));
        Assert.Equal(expected, result);
        Assert.True(Directory.Exists(expected));
    }

    // ── assets-disabled creates no directory ─────────────────────────────────

    [Fact]
    public void Provision_AssetsDisabled_CreatesNothing()
    {
        SessionAssetProvisioner provisioner = new();

        string? result = provisioner.Provision(assetsEnabled: false, _workspace, "sess-xyz");

        Assert.Null(result);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "assets")));
    }

    // ── null session id creates no directory ─────────────────────────────────

    [Fact]
    public void Provision_NullSessionId_CreatesNothing()
    {
        SessionAssetProvisioner provisioner = new();

        string? result = provisioner.Provision(assetsEnabled: true, _workspace, sessionId: null);

        Assert.Null(result);
        Assert.False(Directory.Exists(Path.Combine(_workspace, "assets")));
    }

    // ── idempotent: calling twice does not throw ──────────────────────────────

    [Fact]
    public void Provision_CalledTwice_IsIdempotent()
    {
        SessionAssetProvisioner provisioner = new();
        const string sessionId = "sess-idem";

        string? first = provisioner.Provision(assetsEnabled: true, _workspace, sessionId);
        string? second = provisioner.Provision(assetsEnabled: true, _workspace, sessionId);

        Assert.Equal(first, second);
        Assert.True(Directory.Exists(first!));
    }

    // ── null workspace root falls back to cwd ────────────────────────────────

    [Fact]
    public void Provision_NullWorkspaceRoot_FallsBackToCwd()
    {
        SessionAssetProvisioner provisioner = new();
        const string sessionId = "sess-cwd";

        string? result = provisioner.Provision(assetsEnabled: true, workspaceRoot: null, sessionId);

        // Should succeed and land under cwd.
        Assert.NotNull(result);
        Assert.True(Directory.Exists(result));

        // Cleanup
        Directory.Delete(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets")), recursive: true);
    }

    // ── asset dir is distinct from attachments/ ──────────────────────────────

    [Fact]
    public void Provision_AssetPath_IsDistinctFromAttachmentsDir()
    {
        SessionAssetProvisioner provisioner = new();
        const string sessionId = "sess-dist";

        string? assetPath = provisioner.Provision(assetsEnabled: true, _workspace, sessionId);

        // Session attachments live inside the session-storage directory, e.g.:
        //   <store>/.dmon/sessions/<id>/attachments/
        // The asset path must NOT be under a .dmon subtree.
        Assert.NotNull(assetPath);
        Assert.DoesNotContain(".dmon", assetPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("assets", sessionId), assetPath);
    }
}
