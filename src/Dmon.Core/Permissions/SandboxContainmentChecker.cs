namespace Dmon.Core.Permissions;

/// <summary>
/// Determines whether a filesystem path is contained within a sandbox asset directory,
/// using symlink-resolved, <c>..</c>-collapsed path comparison.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path.GetFullPath"/> collapses <c>..</c> but does NOT resolve symlinks.
/// A symlink inside the asset directory pointing outside (e.g. to <c>/etc</c>), or the
/// target path traversing a symlinked ancestor, is a sandbox escape vector. This class
/// resolves symlinks on the deepest existing ancestor of each path before comparing,
/// so such escapes fail the containment check.
/// </para>
/// <para>
/// The target file or directory need not exist yet (it may be about to be created).
/// Symlink resolution walks upward from the target to the nearest existing ancestor,
/// resolves that ancestor's real path, then re-appends the non-existing tail. The same
/// approach applies to the asset directory itself.
/// </para>
/// <para>
/// A <b>broken symlink</b> (one whose ultimate target does not exist) is detected by
/// link-attribute probing (<see cref="IsSymlink"/>) rather than by
/// <see cref="Path.Exists"/>, which returns <see langword="false"/> for dangling links
/// on Linux. Any entry that is a symlink but cannot be resolved to a real path is
/// treated as un-containable — the gate fails <b>closed</b> (not contained).
/// </para>
/// </remarks>
internal static class SandboxContainmentChecker
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="targetPath"/> is contained
    /// within <paramref name="assetDirectory"/> after both are symlink-resolved.
    /// Returns <see langword="false"/> when either path traverses a broken or
    /// unresolvable symlink (fail-closed).
    /// </summary>
    /// <param name="targetPath">
    /// The path to test. May be relative (it will be made absolute via
    /// <see cref="Path.GetFullPath(string)"/> before resolution).
    /// </param>
    /// <param name="assetDirectory">
    /// The asset directory root (typically the value from
    /// <c>SessionAssetPath.Compute(workspaceRoot, sessionId)</c>).
    /// </param>
    internal static bool IsContained(string targetPath, string assetDirectory)
    {
        string? resolvedTarget = ResolveRealPath(Path.GetFullPath(targetPath));
        string? resolvedAsset  = ResolveRealPath(Path.GetFullPath(assetDirectory));

        // Fail closed: an unresolvable symlink anywhere in either path is not containable.
        if (resolvedTarget is null || resolvedAsset is null)
            return false;

        // Directory-boundary-aware containment: a bare string prefix is not enough.
        // "assets/session-evil" must NOT match "assets/session".
        string assetWithSep = resolvedAsset.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedAsset
            : resolvedAsset + Path.DirectorySeparatorChar;

        return resolvedTarget.StartsWith(assetWithSep, StringComparison.Ordinal)
            || string.Equals(resolvedTarget, resolvedAsset, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the real path of <paramref name="absolutePath"/> by canonicalising the
    /// entire directory chain component-by-component.
    /// </summary>
    /// <returns>
    /// The resolved real path, or <see langword="null"/> if any existing component in
    /// the path is a symlink whose target chain cannot be resolved (broken link).
    /// A non-existent tail (path components that don't exist as filesystem entries)
    /// is safe to append literally and never causes a <see langword="null"/> return.
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>
    ///     The directory portion of the path is always resolved through any symlinks,
    ///     regardless of whether the leaf file/directory itself exists. This prevents a
    ///     symlinked ancestor (e.g. <c>assets/sess/evil → /outside</c>) from being
    ///     invisible when the leaf (<c>assets/sess/evil/passwd</c>) happens to exist as a
    ///     real file, which would otherwise be the case with a leaf-only fast path.
    ///   </item>
    ///   <item>
    ///     Symlink detection uses <see cref="IsSymlink"/>, which reads the link attribute
    ///     without following the target — so broken/dangling symlinks are detected on
    ///     every platform, not just platforms where <see cref="Path.Exists"/> returns
    ///     <see langword="true"/> for dangling links.
    ///   </item>
    ///   <item>
    ///     If the leaf segment is itself a symlink (e.g. a symlink directly inside the
    ///     asset directory), it is also followed after the parent is resolved.
    ///   </item>
    ///   <item>
    ///     If one or more segments do not exist (and are not symlinks), the deepest
    ///     existing ancestor is resolved and the non-existing tail is re-appended.
    ///   </item>
    ///   <item>
    ///     If no ancestor exists (fully hypothetical path), the input is returned
    ///     unchanged — no symlink tricks are possible in a fully non-existent tree.
    ///   </item>
    /// </list>
    /// </remarks>
    internal static string? ResolveRealPath(string absolutePath)
    {
        string? parent = Path.GetDirectoryName(absolutePath);
        string leaf = Path.GetFileName(absolutePath);

        // Filesystem root or a path with no separable leaf: resolve the whole path as a
        // directory (it may itself be a symlink).
        if (parent is null || leaf.Length == 0)
        {
            return ResolveExistingAncestor(absolutePath, tail: null);
        }

        // Always resolve the directory portion first — this catches symlinked ancestors
        // even when the leaf file exists as a regular file.
        string? resolvedParent = ResolveExistingAncestor(parent, tail: null);
        if (resolvedParent is null)
            return null;

        string candidate = Path.Combine(resolvedParent, leaf);

        // If the resolved candidate itself is a symlink (detected by link attribute, not
        // by Path.Exists — so broken/dangling symlinks are caught on all platforms), follow
        // it to its final target. A broken symlink here means the leaf names an
        // out-of-sandbox destination whose target no longer exists — fail closed.
        if (IsSymlink(candidate))
        {
            string? followed = FollowLinkToTarget(candidate);
            // Null means the symlink chain is broken or unresolvable — fail closed.
            return followed;
        }

        return candidate;
    }

    /// <summary>
    /// Walks upward from <paramref name="absoluteDir"/> to find the deepest existing
    /// (or symlinked) ancestor, resolves that ancestor through any symlinks, and
    /// re-appends <paramref name="tail"/> (the non-existing suffix collected so far).
    /// </summary>
    /// <returns>
    /// The resolved path with <paramref name="tail"/> re-appended, or
    /// <see langword="null"/> if any existing ancestor component is a symlink whose
    /// target chain cannot be resolved.
    /// </returns>
    private static string? ResolveExistingAncestor(string absoluteDir, string? tail)
    {
        // Determine the nature of this path component:
        //   - IsSymlink uses link-attribute detection (non-null LinkTarget), which is true
        //     for both live and dangling symlinks, on all platforms. This is the correct
        //     primitive here because Path.Exists returns false for dangling symlinks on
        //     Linux, which would otherwise cause dangling links to be silently skipped and
        //     their literal path components re-appended (fail-open).
        bool link = IsSymlink(absoluteDir);
        bool present = link || Path.Exists(absoluteDir);

        if (present)
        {
            if (link)
            {
                // This entry is a symlink (live or dangling). Follow the full chain.
                // A broken/unresolvable chain returns null — fail closed.
                string? resolved = FollowLinkToTarget(absoluteDir);
                if (resolved is null)
                    return null;
                return tail is null ? resolved : Path.Combine(resolved, tail);
            }

            // Regular directory (not a symlink).
            return tail is null ? absoluteDir : Path.Combine(absoluteDir, tail);
        }

        string? parent = Path.GetDirectoryName(absoluteDir);

        // Reached the filesystem root with no existing ancestor found.
        if (parent is null || string.Equals(parent, absoluteDir, StringComparison.Ordinal))
        {
            // No symlink tricks possible in a fully non-existent tree; return as-is.
            return tail is null ? absoluteDir : Path.Combine(absoluteDir, tail);
        }

        string segment = Path.GetFileName(absoluteDir);
        string newTail = tail is null ? segment : Path.Combine(segment, tail);
        return ResolveExistingAncestor(parent, newTail);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> names a symlink entry,
    /// regardless of whether the symlink's target exists (i.e. works for dangling links).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="FileInfo.LinkTarget"/> / <see cref="DirectoryInfo.LinkTarget"/>,
    /// which read the link attribute without following the target chain and do not throw
    /// for dangling links. This is the correct primitive for "is this entry a symlink"
    /// on all platforms — <see cref="Path.Exists"/> must NOT be used for this purpose
    /// because it returns <see langword="false"/> for dangling symlinks on Linux.
    /// </remarks>
    private static bool IsSymlink(string path)
    {
        // FileInfo.LinkTarget is non-null for any symlink (file or dir), live or dangling.
        // DirectoryInfo.LinkTarget covers the same; checking both handles all entry types.
        return new FileInfo(path).LinkTarget is not null
            || new DirectoryInfo(path).LinkTarget is not null;
    }

    /// <summary>
    /// Follows a symlink (or chain of symlinks) to its final real target, returning the
    /// absolute path of the ultimate target.
    /// </summary>
    /// <returns>
    /// The resolved absolute path, or <see langword="null"/> when <paramref name="path"/>
    /// is not a symlink, the target cannot be resolved (broken/dangling link), or a
    /// permission error prevents resolution.
    /// </returns>
    private static string? FollowLinkToTarget(string path)
    {
        try
        {
            // ResolveLinkTarget(returnFinalTarget: true) follows the full chain.
            // On Linux it throws IOException for dangling symlinks (target does not exist).
            // On macOS it returns the (non-existent) target path without throwing.
            // Either way, a non-null result that is not rooted is re-anchored below.
            string? final = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                         ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;

            // If the result is still a relative path (uncommon but possible on some platforms),
            // re-anchor it against the link's parent directory.
            if (final is not null && !Path.IsPathRooted(final))
            {
                string? dir = Path.GetDirectoryName(path);
                final = dir is null ? final : Path.GetFullPath(Path.Combine(dir, final));
            }

            return final;
        }
        catch (IOException)
        {
            // Broken symlink (Linux: target does not exist) or permission error.
            // Fail closed: return null so the caller rejects the path.
            return null;
        }
    }
}
