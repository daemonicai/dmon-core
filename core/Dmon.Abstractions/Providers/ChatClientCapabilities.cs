namespace Dmon.Abstractions.Providers;

public sealed class ChatClientCapabilities
{
    public bool SupportsToolCalling { get; init; }
    public bool SupportsReasoning { get; init; }
    public int ContextWindow { get; init; }
    public int MaxTokens { get; init; }
}
