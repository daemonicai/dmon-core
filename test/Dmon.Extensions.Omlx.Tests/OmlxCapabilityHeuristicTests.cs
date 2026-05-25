using Dmon.Abstractions.Providers;
using Dmon.Extensions.Omlx;

namespace Dmon.Extensions.Omlx.Tests;

public sealed class OmlxCapabilityHeuristicTests
{
    // Embedding / reranking — no tool calling, no reasoning
    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("text-embedding-3-large")]
    [InlineData("bge-reranker-v2-m3")]
    [InlineData("model-e-001")]
    public void Infer_EmbeddingOrRerank_NoToolsNoReasoning(string modelId)
    {
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer(modelId);
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // Reasoning models — tools + reasoning
    [Theory]
    [InlineData("qwen3-8b-4bit")]
    [InlineData("qwen3-14b")]
    [InlineData("deepseek-r1-0528")]
    [InlineData("phi4-reasoning")]
    [InlineData("qwen2.5-thinking-32b")]
    public void Infer_ReasoningModel_ToolsAndReasoning(string modelId)
    {
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer(modelId);
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    // Instruction-tuned / chat models — tools, no reasoning
    [Theory]
    [InlineData("gemma-4-e4b-it-4bit")]
    [InlineData("llama3.1-instruct")]
    [InlineData("mistral-7b-instruct-v0.3")]
    [InlineData("llama-3-8b-chat")]
    public void Infer_InstructOrChat_ToolsNoReasoning(string modelId)
    {
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer(modelId);
        Assert.True(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // Vision / multimodal — tools, no reasoning
    [Theory]
    [InlineData("llava-1.6-mistral-vlm")]
    [InlineData("qwen2-vl-7b-instruct")]
    [InlineData("phi-3-vision")]
    [InlineData("model-vl-4bit")]
    public void Infer_VisionModel_ToolsNoReasoning(string modelId)
    {
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer(modelId);
        Assert.True(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // Unrecognised — conservative defaults
    [Theory]
    [InlineData("unknown-model-xyz")]
    [InlineData("some-random-base-7b")]
    [InlineData("")]
    public void Infer_Unrecognised_ConservativeDefaults(string modelId)
    {
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer(modelId);
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // Priority: embed/rerank beats reasoning pattern when both match
    [Fact]
    public void Infer_EmbedPatternBeatsReasoning_WhenBothMatch()
    {
        // "rerank" appears before the reasoning check, so it wins
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer("bge-reranker-r1");
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    // Case-insensitive matching
    [Fact]
    public void Infer_CaseInsensitive_ReasoningModel()
    {
        // Mixed-case qwen3 model → should match reasoning branch
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer("Qwen3-8B-4Bit");
        Assert.True(caps.SupportsToolCalling);
        Assert.True(caps.SupportsReasoning);
    }

    [Fact]
    public void Infer_CaseInsensitive_InstructModel()
    {
        // Mixed-case -it- model → should match instruction-tuned branch
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer("Gemma-4-E4B-IT-4Bit");
        Assert.True(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }

    [Fact]
    public void Infer_CaseInsensitive_EmbedModel()
    {
        // All-caps embed model → should match embedding branch
        ChatClientCapabilities caps = OmlxCapabilityHeuristic.Infer("NOMIC-EMBED-TEXT");
        Assert.False(caps.SupportsToolCalling);
        Assert.False(caps.SupportsReasoning);
    }
}
