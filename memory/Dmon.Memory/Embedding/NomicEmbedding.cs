namespace Dmon.Memory.Embedding;

/// <summary>
/// Constants and pure helpers for the nomic-embed-text-v1.5 model.
///
/// Prefix split: the generator (<see cref="LocalEmbeddingGenerator"/>) is prefix-agnostic.
/// Call sites in group 3 must apply prefixes via <see cref="ApplyDocumentPrefix"/> or
/// <see cref="ApplyQueryPrefix"/> before passing text to <c>IEmbeddingGenerator</c>.
/// </summary>
public static class NomicEmbedding
{
    /// <summary>HuggingFace model identifier.</summary>
    public const string ModelId = "nomic-embed-text-v1.5";

    /// <summary>Full embedding dimension (768). No Matryoshka truncation in use.</summary>
    public const int Dimensions = 768;

    /// <summary>
    /// Recommended GGUF variant on HuggingFace. Q4_K_M balances size (~110 MB) and quality.
    /// Source: <c>nomic-ai/nomic-embed-text-v1.5-GGUF</c>
    /// </summary>
    public const string GgufFileName = "nomic-embed-text-v1.5.Q4_K_M.gguf";

    /// <summary>
    /// HuggingFace download URL for the GGUF. Resolved from the canonical HF repo.
    /// </summary>
    public const string GgufDownloadUrl =
        "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/"
        + GgufFileName;

    // nomic dual task-prefixes — applied at store / query call sites, NOT inside the generator.
    public const string DocumentPrefix = "search_document: ";
    public const string QueryPrefix = "search_query: ";

    /// <summary>
    /// Prepend the <c>search_document:</c> prefix for text being stored (indexed).
    /// Call this at the store call site, not inside the embedding generator.
    /// </summary>
    public static string ApplyDocumentPrefix(string text) => DocumentPrefix + text;

    /// <summary>
    /// Prepend the <c>search_query:</c> prefix for a retrieval query.
    /// Call this at the query call site, not inside the embedding generator.
    /// </summary>
    public static string ApplyQueryPrefix(string text) => QueryPrefix + text;
}
