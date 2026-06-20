using Microsoft.Extensions.AI;

namespace Dmail.Services;

public sealed class EmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingService> _logger;
    private volatile bool _modelReady;

    public bool IsModelReady => _modelReady;
    public const int EmbeddingDimensions = 384;
    public const int MaxTokenCount = 512;
    public const int MaxBatchSize = 32;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILogger<EmbeddingService> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    /// <summary>
    /// Task 4.5: Validate the ONNX model at startup by running a test embedding
    /// and verifying 384-dimension output.
    /// </summary>
    public async Task<bool> ValidateModelAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Validating ONNX embedding model...");
            var result = await _generator.GenerateAsync(["validation test"], cancellationToken: ct);
            if (result.Count == 1 && result[0].Vector.Length == EmbeddingDimensions)
            {
                _modelReady = true;
                _logger.LogInformation("ONNX model validated — {Dimensions}-dimension embeddings", EmbeddingDimensions);
                return true;
            }

            _logger.LogError("Model validation failed: got {Count} results, first has {Dim} dimensions",
                result.Count, result.Count > 0 ? result[0].Vector.Length : 0);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model validation threw exception");
            return false;
        }
    }

    /// <summary>
    /// Generate a single embedding from combined email text.
    /// </summary>
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var results = await _generator.GenerateAsync([text], cancellationToken: ct);
        return results[0].Vector.ToArray();
    }

    /// <summary>
    /// Task 4.4: Batch embedding for backfill throughput.
    /// Supports up to MaxBatchSize (32) texts per inference call.
    /// </summary>
    public async Task<float[][]> GenerateBatchEmbeddingsAsync(string[] texts, CancellationToken ct = default)
    {
        var allVectors = new float[texts.Length][];

        for (int i = 0; i < texts.Length; i += MaxBatchSize)
        {
            var batch = texts.Skip(i).Take(MaxBatchSize).ToArray();
            var results = await _generator.GenerateAsync(batch, cancellationToken: ct);

            for (int j = 0; j < batch.Length; j++)
            {
                allVectors[i + j] = results[j].Vector.ToArray();
            }
        }

        return allVectors;
    }

    /// <summary>
    /// Task 4.3: Extract embedding input from email subject + body.
    /// Concatenates subject and body with newline, truncates to 512 tokens.
    /// Uses a simple heuristic: ~4 chars per token for English text.
    /// </summary>
    public static string BuildEmbeddingText(string subject, string body)
    {
        var combined = $"{subject}\n{body}";
        // Conservative: ~4 chars per token, 512 tokens = ~2048 chars
        const int maxChars = MaxTokenCount * 4;
        if (combined.Length > maxChars)
        {
            combined = combined[..maxChars];
        }
        return combined;
    }
}
