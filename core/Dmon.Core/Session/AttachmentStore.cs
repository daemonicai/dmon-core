using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dmon.Core.Session;

public interface IAttachmentStore
{
    /// <summary>
    /// Returns null if content is within threshold; otherwise writes to attachments/ and returns the relative path.
    /// </summary>
    Task<string?> StoreIfLargeAsync(
        string sessionId,
        string callId,
        string content,
        string extension = "txt",
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentStore : IAttachmentStore
{
    private readonly ISessionDirectoryResolver _resolver;
    private readonly IConfiguration _configuration;

    // Only chars safe in all filesystems and unambiguous in URLs/paths.
    private static readonly Regex SafeCallIdPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public AttachmentStore(ISessionDirectoryResolver resolver, IConfiguration configuration)
    {
        _resolver = resolver;
        _configuration = configuration;
    }

    public async Task<string?> StoreIfLargeAsync(
        string sessionId,
        string callId,
        string content,
        string extension = "txt",
        CancellationToken cancellationToken = default)
    {
        int threshold = _configuration.GetValue("Dmon:Session:AttachmentThresholdBytes", 1024);
        int byteCount = Encoding.UTF8.GetByteCount(content);

        if (byteCount <= threshold)
        {
            return null;
        }

        string root = _resolver.Resolve(Environment.CurrentDirectory);
        string attachmentsDir = Path.Combine(root, sessionId, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        string baseName = IsSafeCallId(callId) ? callId : DeriveFilename(callId);
        string fileName = $"{baseName}.{extension}";

        // Containment guard: resolve the full path and assert it stays inside attachmentsDir.
        string resolvedAttachmentsDir = Path.GetFullPath(attachmentsDir) + Path.DirectorySeparatorChar;
        string filePath = Path.GetFullPath(Path.Combine(attachmentsDir, fileName));

        if (!filePath.StartsWith(resolvedAttachmentsDir, StringComparison.Ordinal))
        {
            // The derived filename should never escape — this is a defensive assertion against implementation bugs.
            throw new InvalidOperationException(
                $"Attachment path '{filePath}' escapes the attachments directory '{resolvedAttachmentsDir}'. This is a bug.");
        }

        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return $"attachments/{fileName}";
    }

    private static bool IsSafeCallId(string callId)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return false;
        }

        // ".." is allowlisted by the regex char class but must be rejected — path traversal.
        if (callId.Contains(".."))
        {
            return false;
        }

        return SafeCallIdPattern.IsMatch(callId);
    }

    // Uses the full SHA-256 hex digest so distinct ids always map to distinct names.
    private static string DeriveFilename(string callId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(callId));
        return "unsafe_" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
