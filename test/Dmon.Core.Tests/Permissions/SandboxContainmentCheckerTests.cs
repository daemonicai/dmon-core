using Dmon.Core.Permissions;

namespace Dmon.Core.Tests.Permissions;

/// <summary>
/// Escape-matrix tests for <see cref="SandboxContainmentChecker"/>.
/// All cases that exercise symlinks use real temp directories and real symlinks.
/// </summary>
public sealed class SandboxContainmentCheckerTests : IDisposable
{
    private readonly string _root;

    public SandboxContainmentCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dmon-sandbox-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* best-effort cleanup — catches IOException and UnauthorizedAccessException from symlink-heavy trees */ }
    }

    // -------------------------------------------------------------------------
    // Case 1: Plain existing file directly inside the asset dir → contained
    // -------------------------------------------------------------------------
    [Fact]
    public void ExistingFileInsideAssetDir_IsContained()
    {
        string assetDir = MakeDir("assets/sess1");
        string file = Path.Combine(assetDir, "notes.txt");
        File.WriteAllText(file, "hello");

        Assert.True(SandboxContainmentChecker.IsContained(file, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 2: New (not-yet-existing) file inside the asset dir → contained
    // -------------------------------------------------------------------------
    [Fact]
    public void NonExistingFileInsideAssetDir_IsContained()
    {
        string assetDir = MakeDir("assets/sess2");
        string file = Path.Combine(assetDir, "new-output.txt");

        // file does not exist yet
        Assert.False(File.Exists(file));
        Assert.True(SandboxContainmentChecker.IsContained(file, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 3: ".." escape → NOT contained
    // -------------------------------------------------------------------------
    [Fact]
    public void DotDotEscape_IsNotContained()
    {
        string assetDir = MakeDir("assets/sess3");
        // /tmp/.../assets/sess3/../../etc/passwd → normalises to /tmp/.../etc/passwd
        string escapePath = Path.Combine(assetDir, "..", "..", "etc", "passwd");

        Assert.False(SandboxContainmentChecker.IsContained(escapePath, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 4: Sibling-prefix: target in assets/<id>-evil vs asset dir assets/<id>
    // -------------------------------------------------------------------------
    [Fact]
    public void SiblingPrefixDir_IsNotContained()
    {
        string assetDir = MakeDir("assets/sess4");
        string evilDir = MakeDir("assets/sess4-evil");
        string file = Path.Combine(evilDir, "secret.txt");

        Assert.False(SandboxContainmentChecker.IsContained(file, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 5 (THE BLOCKER REPRO): existing file under an in-asset-dir symlink
    // pointing outside → NOT contained
    // assets/<id>/evil -> <outside>
    // /outside/passwd exists
    // target = assets/<id>/evil/passwd
    // -------------------------------------------------------------------------
    [Fact]
    public void ExistingFileUnderSymlinkedAncestorPointingOutside_IsNotContained()
    {
        string assetDir = MakeDir("assets/sess5");
        string outside = MakeDir("outside5");

        // Create a real file in the outside directory
        string outsideFile = Path.Combine(outside, "passwd");
        File.WriteAllText(outsideFile, "root:x:0:0");

        // Create a symlink inside the asset dir pointing to outside
        string evilLink = Path.Combine(assetDir, "evil");
        Directory.CreateSymbolicLink(evilLink, outside);

        // The target traverses the symlink to reach the existing outside file
        string target = Path.Combine(assetDir, "evil", "passwd");

        // Confirm the file is reachable (so Path.Exists would return true)
        Assert.True(File.Exists(target), "Pre-condition: target file must be reachable through the symlink");

        // The blocker: this must be REJECTED
        Assert.False(SandboxContainmentChecker.IsContained(target, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 6: New (not-yet-existing) file under the same in-asset-dir symlink
    // pointing outside → NOT contained
    // -------------------------------------------------------------------------
    [Fact]
    public void NewFileUnderSymlinkedAncestorPointingOutside_IsNotContained()
    {
        string assetDir = MakeDir("assets/sess6");
        string outside = MakeDir("outside6");

        // Symlink inside the asset dir pointing to outside
        string evilLink = Path.Combine(assetDir, "evil");
        Directory.CreateSymbolicLink(evilLink, outside);

        // The new file does not exist yet
        string target = Path.Combine(assetDir, "evil", "newfile.txt");
        Assert.False(File.Exists(target), "Pre-condition: new file must not exist yet");

        Assert.False(SandboxContainmentChecker.IsContained(target, assetDir));
    }

    // -------------------------------------------------------------------------
    // Case 7: Symlinked asset dir itself (asset dir is a symlink to a real dir)
    // → a legitimate file inside the real target is contained
    // -------------------------------------------------------------------------
    [Fact]
    public void SymlinkedAssetDir_LegitimateFileInsideRealTarget_IsContained()
    {
        string realDir = MakeDir("real-assets7");
        string file = Path.Combine(realDir, "data.bin");
        File.WriteAllText(file, "payload");

        // Create the symlinked asset directory
        string symlinkAssetDir = Path.Combine(_root, "assets-link7");
        Directory.CreateSymbolicLink(symlinkAssetDir, realDir);

        // IsContained uses the symlink path as the asset directory argument
        string target = Path.Combine(symlinkAssetDir, "data.bin");

        Assert.True(SandboxContainmentChecker.IsContained(target, symlinkAssetDir));
    }

    // -------------------------------------------------------------------------
    // Case 8: Broken intermediate symlink in path → NOT contained (fail closed), no throw
    // assetDir/broken -> <deleted-outside>   (dangling directory symlink)
    // target = assetDir/broken/file.txt
    //
    // A dangling symlink IS still a symlink that redirects writes outside the sandbox.
    // Path.Exists returns false for dangling symlinks on Linux, so code that gates on
    // Path.Exists would skip the link, treat "broken" as a literal non-existent directory
    // under assetDir, re-append "file.txt", and produce a lexically-inside path → fail-open.
    // The fix detects the link by attribute (FileInfo.LinkTarget / DirectoryInfo.LinkTarget),
    // which is non-null for any symlink regardless of whether its target exists, then fails
    // closed when the target cannot be resolved (broken chain).
    // -------------------------------------------------------------------------
    [Fact]
    public void BrokenIntermediateSymlink_IsNotContainedAndDoesNotThrow()
    {
        string assetDir = MakeDir("assets/sess8");
        string outside = MakeDir("outside8");

        // Create a symlink inside the asset dir pointing to outside, then break it.
        string brokenLink = Path.Combine(assetDir, "broken");
        Directory.CreateSymbolicLink(brokenLink, outside);
        Directory.Delete(outside);

        string target = Path.Combine(assetDir, "broken", "file.txt");

        // Must not throw, and must NOT be contained (fail closed on all platforms).
        bool result = SandboxContainmentChecker.IsContained(target, assetDir);
        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // Case 9: Broken leaf symlink → NOT contained (fail closed), no throw
    // assetDir/out.txt -> <deleted-outside-file>   (dangling file symlink)
    // target = assetDir/out.txt
    //
    // A dangling file symlink at the leaf position is the same class of defect:
    // IsContained(assetDir/out.txt, assetDir) must return false because the symlink
    // names an out-of-sandbox file whose target no longer exists. On Linux, Path.Exists
    // for a dangling symlink returns false, so a Path.Exists-gated leaf-resolution would
    // skip following the link and return the lexically-inside candidate → fail-open.
    // The fix applies the same IsSymlink + fail-closed path to the leaf site.
    // -------------------------------------------------------------------------
    [Fact]
    public void BrokenLeafSymlink_IsNotContainedAndDoesNotThrow()
    {
        string assetDir = MakeDir("assets/sess9");

        // Create an outside file, symlink it from inside the asset dir, then delete it.
        string outsideDir = MakeDir("outside9");
        string outsideFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "sensitive");

        string leafLink = Path.Combine(assetDir, "out.txt");
        File.CreateSymbolicLink(leafLink, outsideFile);
        File.Delete(outsideFile); // break the symlink — leaf now points outside to a deleted file

        // A dangling leaf symlink pointing outside the sandbox must be rejected.
        bool result = SandboxContainmentChecker.IsContained(leafLink, assetDir);
        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // Case 10: Broken intermediate symlink (multi-level tail) → NOT contained
    // assetDir/a -> <deleted>   with target assetDir/a/b/c.txt
    //
    // Deeper tail after a dangling intermediate link: same fail-closed guarantee.
    // -------------------------------------------------------------------------
    [Fact]
    public void BrokenIntermediateSymlinkWithDeepTail_IsNotContainedAndDoesNotThrow()
    {
        string assetDir = MakeDir("assets/sess10");
        string outside = MakeDir("outside10");

        string brokenLink = Path.Combine(assetDir, "a");
        Directory.CreateSymbolicLink(brokenLink, outside);
        Directory.Delete(outside);

        // Multi-level tail after the dangling link.
        string target = Path.Combine(assetDir, "a", "b", "c.txt");

        bool result = SandboxContainmentChecker.IsContained(target, assetDir);
        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------
    private string MakeDir(string relativePath)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }
}
