using System.Runtime.CompilerServices;
using System.Text.Json;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Pipeline;

/// <summary>
/// IChatClient middleware that offloads large FunctionResultContent strings to session
/// attachments before they are forwarded to the LLM. Positioned inside
/// FunctionInvokingChatClient so tool results appear in the input messages on re-entry.
///
/// Pipeline order:
///   PermissionGateChatClient → FunctionInvokingChatClient → AttachmentOffloadingChatClient
///     → RetryingChatClient → provider
/// </summary>
public sealed class AttachmentOffloadingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ISessionHandler _sessionHandler;
    private readonly IAttachmentStore _attachmentStore;

    public AttachmentOffloadingChatClient(
        IChatClient inner,
        ISessionHandler sessionHandler,
        IAttachmentStore attachmentStore)
    {
        _inner = inner;
        _sessionHandler = sessionHandler;
        _attachmentStore = attachmentStore;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> processed = await ProcessInputMessagesAsync(
            messages, cancellationToken).ConfigureAwait(false);
        return await _inner.GetResponseAsync(processed, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> processed = await ProcessInputMessagesAsync(
            messages, cancellationToken).ConfigureAwait(false);

        await foreach (ChatResponseUpdate update in
            _inner.GetStreamingResponseAsync(processed, options, cancellationToken)
                .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private async Task<IReadOnlyList<ChatMessage>> ProcessInputMessagesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        SessionMeta? session = _sessionHandler.CurrentSession;
        List<ChatMessage> result = [];

        foreach (ChatMessage message in messages)
        {
            if (session is null || message.Role != ChatRole.Tool)
            {
                result.Add(message);
                continue;
            }

            List<AIContent> processedContents = [];
            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionResultContent resultContent
                    && resultContent.Result?.ToString() is string resultText)
                {
                    string? attachmentPath = await _attachmentStore.StoreIfLargeAsync(
                        session.Id,
                        resultContent.CallId,
                        resultText,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (attachmentPath is not null)
                    {
                        string preview = resultText.Length > 200
                            ? resultText[..200] + "..."
                            : resultText;
                        string replacement = JsonSerializer.Serialize(new
                        {
                            attachmentPath,
                            preview
                        });
                        processedContents.Add(new FunctionResultContent(resultContent.CallId, replacement));
                        continue;
                    }
                }

                processedContents.Add(content);
            }

            result.Add(new ChatMessage(message.Role, processedContents));
        }

        return result;
    }
}
