using Microsoft.Extensions.AI;

namespace Dmon.Abstractions;

public interface ISystemPromptBuilder
{
    Task<ChatMessage> BuildAsync(CancellationToken cancellationToken);
}
