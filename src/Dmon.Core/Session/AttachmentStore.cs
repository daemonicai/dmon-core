using System.Text;

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

        string fileName = $"{callId}.{extension}";
        string filePath = Path.Combine(attachmentsDir, fileName);

        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return $"attachments/{fileName}";
    }
}
