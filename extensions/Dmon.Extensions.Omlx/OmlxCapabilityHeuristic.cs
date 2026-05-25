using Dmon.Abstractions.Providers;

namespace Dmon.Extensions.Omlx;

public static class OmlxCapabilityHeuristic
{
    public static ChatClientCapabilities Infer(string modelId)
    {
        string lower = (modelId ?? string.Empty).ToLowerInvariant();

        // Embedding / reranking models — no tool calling, no reasoning
        if (lower.Contains("embed") || lower.Contains("-e-") || lower.Contains("rerank"))
            return new ChatClientCapabilities { SupportsToolCalling = false, SupportsReasoning = false };

        // Reasoning models
        if (lower.StartsWith("qwen3") || lower.Contains("thinking") || lower.Contains("-r1") || lower.Contains("reason"))
            return new ChatClientCapabilities { SupportsToolCalling = true, SupportsReasoning = true };

        // Instruction-tuned chat models
        if (lower.Contains("-it-") || lower.Contains("instruct") || lower.Contains("-chat"))
            return new ChatClientCapabilities { SupportsToolCalling = true, SupportsReasoning = false };

        // Vision / multimodal models
        if (lower.Contains("vlm") || lower.Contains("vision") || lower.Contains("-vl-"))
            return new ChatClientCapabilities { SupportsToolCalling = true, SupportsReasoning = false };

        // Unrecognised — conservative defaults
        return new ChatClientCapabilities { SupportsToolCalling = false, SupportsReasoning = false };
    }
}
