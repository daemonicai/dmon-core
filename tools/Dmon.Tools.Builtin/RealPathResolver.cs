namespace Dmon.Tools.Builtin;

/// <summary>
/// Resolves the real, symlink-followed path of an absolute filesystem path, mirroring
/// the engine's sandbox containment resolution so the builtin tools can make fail-closed
/// containment decisions without depending on <c>Dmon.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path.GetFullPath(string)"/> collapses <c>..</c> but does NOT resolve
/// symlinks. A symlink whose target escapes the current working directory is therefore
/// invisible to a plain <c>GetFullPath</c> containment check. This resolver canonicalises
/// the directory chain through symlinks before the caller compares against a root.
/// </para>
/// <para>
/// A path that cannot be resolved (a broken/dangling symlink, or a symlink chain that
/// cannot be followed) resolves to <see langword="null"/> — the caller is expected to
/// fail closed on <see langword="null"/>.
/// </para>
/// </remarks>
internal static class RealPathResolver
{
    /// <summary>
    /// Resolves the real path of <paramref name="absolutePath"/> by canonicalising the
    /// directory chain component-by-component and following a leaf symlink.
    /// </summary>
    /// <returns>
    /// The resolved real path, or <see langword="null"/> if any existing component is a
    /// symlink whose target chain cannot be resolved (broken link). A non-existent tail
    /// is re-appended literally and never causes a <see langword="null"/> return.
    /// </returns>
    internal static string? ResolveRealPath(string absolutePath)
    {
        string? parent = Path.GetDirectoryName(absolutePath);
        string leaf = Path.GetFileName(absolutePath);

        if (parent is null || leaf.Length == 0)
        {
            return ResolveExistingAncestor(absolutePath, tail: null);
        }

        string? resolvedParent = ResolveExistingAncestor(parent, tail: null);
        if (resolvedParent is null)
            return null;

        string candidate = Path.Combine(resolvedParent, leaf);

        if (IsSymlink(candidate))
        {
            return FollowLinkToTarget(candidate);
        }

        return candidate;
    }

    private static string? ResolveExistingAncestor(string absoluteDir, string? tail)
    {
        bool link = IsSymlink(absoluteDir);
        bool present = link || Path.Exists(absoluteDir);

        if (present)
        {
            if (link)
            {
                string? resolved = FollowLinkToTarget(absoluteDir);
                if (resolved is null)
                    return null;
                return tail is null ? resolved : Path.Combine(resolved, tail);
            }

            return tail is null ? absoluteDir : Path.Combine(absoluteDir, tail);
        }

        string? parent = Path.GetDirectoryName(absoluteDir);

        if (parent is null || string.Equals(parent, absoluteDir, StringComparison.Ordinal))
        {
            return tail is null ? absoluteDir : Path.Combine(absoluteDir, tail);
        }

        string segment = Path.GetFileName(absoluteDir);
        string newTail = tail is null ? segment : Path.Combine(segment, tail);
        return ResolveExistingAncestor(parent, newTail);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> names a symlink entry,
    /// regardless of whether its target exists (works for dangling links on all platforms).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="FileInfo.LinkTarget"/> / <see cref="DirectoryInfo.LinkTarget"/>, which
    /// read the link attribute without following the target. <see cref="Path.Exists(string)"/>
    /// must NOT be used here because it returns <see langword="false"/> for dangling symlinks
    /// on Linux, which would fail open.
    /// </remarks>
    private static bool IsSymlink(string path)
    {
        return new FileInfo(path).LinkTarget is not null
            || new DirectoryInfo(path).LinkTarget is not null;
    }

    /// <summary>
    /// Follows a symlink (or chain of symlinks) to its final real target, returning the
    /// absolute path of the ultimate target, or <see langword="null"/> when the target
    /// cannot be resolved (broken/dangling link) or a permission error prevents resolution.
    /// </summary>
    private static string? FollowLinkToTarget(string path)
    {
        try
        {
            string? final = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                         ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;

            if (final is not null && !Path.IsPathRooted(final))
            {
                string? dir = Path.GetDirectoryName(path);
                final = dir is null ? final : Path.GetFullPath(Path.Combine(dir, final));
            }

            return final;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
