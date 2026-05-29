using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory.Embedding;

/// <summary>
/// Long-lived singleton <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over
/// <see cref="LLamaEmbedder"/>, configured for <c>nomic-embed-text-v1.5</c>.
///
/// Thread-safety: <see cref="LLamaEmbedder"/> is NOT thread-safe. Access is serialized
/// via a <see cref="SemaphoreSlim"/>(1,1) so concurrent callers queue safely.
///
/// Model loading is lazy — weights are loaded on the first <see cref="GenerateAsync"/> call,
/// not in the constructor. Constructing this type does not require the GGUF to be present.
///
/// Normalization: every output vector is L2-normalized before being returned.
/// LlamaSharp does NOT normalize by default.
///
/// Prefixes: this class is prefix-agnostic. Callers must apply <see cref="NomicEmbedding.ApplyDocumentPrefix"/>
/// or <see cref="NomicEmbedding.ApplyQueryPrefix"/> before passing text in.
/// </summary>
public sealed class LocalEmbeddingGenerator
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ModelResolver _resolver;
    private readonly ILogger<LocalEmbeddingGenerator>? _logger;

    // SemaphoreSlim(1,1) serializes all calls into the non-thread-safe LLamaEmbedder.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Lazy-initialized on first use.
    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private bool _disposed;

    public LocalEmbeddingGenerator(
        ModelResolver resolver,
        ILogger<LocalEmbeddingGenerator>? logger = null)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    public EmbeddingGeneratorMetadata Metadata { get; } = new(
        providerName: "llamasharp-local",
        providerUri: null,
        defaultModelId: NomicEmbedding.ModelId,
        defaultModelDimensions: NomicEmbedding.Dimensions);

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(EmbeddingGeneratorMetadata))
            return Metadata;
        if (serviceType.IsInstanceOfType(this))
            return this;
        return null;
    }

    /// <summary>
    /// Embeds a batch of inputs. All inputs are embedded under a single lock acquisition
    /// for efficient ingest — the lock is held for the whole batch, not per-item.
    /// Returns one <see cref="Embedding{T}"/> per input, in the same order.
    /// </summary>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialise before entering the lock to avoid holding it during enumeration.
        string[] inputs = values as string[] ?? values.ToArray();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check _disposed inside the lock so we never race with Dispose().
            ObjectDisposedException.ThrowIf(_disposed, this);

            LLamaEmbedder embedder = await EnsureEmbedderAsync(cancellationToken)
                .ConfigureAwait(false);

            Embedding<float>[] results = new Embedding<float>[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                IReadOnlyList<float[]> raw = await embedder
                    .GetEmbeddings(inputs[i], cancellationToken)
                    .ConfigureAwait(false);

                // Mean-pooling model returns exactly one vector; guard against corrupt/wrong model.
                if (raw.Count == 0)
                    throw new InvalidOperationException(
                        $"Embedding model '{NomicEmbedding.ModelId}' returned no vectors for input[{i}]. " +
                        "Verify that the GGUF file is the correct model and not corrupt.");

                if (raw[0].Length != NomicEmbedding.Dimensions)
                    throw new InvalidOperationException(
                        $"Embedding model returned a vector of length {raw[0].Length} but " +
                        $"{NomicEmbedding.Dimensions} ({NomicEmbedding.ModelId}) was expected. " +
                        "The cached GGUF may be a different model variant; delete it to trigger a fresh download.");

                float[] vector = EmbeddingMath.L2NormalizeInPlace(raw[0]);
                results[i] = new Embedding<float>(vector);
            }

            return new GeneratedEmbeddings<Embedding<float>>(results);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Convenience overload: embed a single string. Delegates to <see cref="GenerateAsync"/>.
    /// </summary>
    public async Task<float[]> EmbedSingleAsync(string text, CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> result = await GenerateAsync(
            [text], cancellationToken: cancellationToken).ConfigureAwait(false);
        return result[0].Vector.ToArray();
    }

    // Ensures weights + embedder are loaded; must be called inside the semaphore.
    private async Task<LLamaEmbedder> EnsureEmbedderAsync(CancellationToken cancellationToken)
    {
        if (_embedder is not null)
            return _embedder;

        string modelPath = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Loading embedding model from {ModelPath}", modelPath);

        ModelParams modelParams = new(modelPath)
        {
            PoolingType = LLamaPoolingType.Mean,
            Embeddings = true,
        };

        _weights = LLamaWeights.LoadFromFile(modelParams);
        _embedder = new LLamaEmbedder(_weights, modelParams);
        return _embedder;
    }

    /// <summary>
    /// Disposes the generator and releases the underlying LLamaSharp native handles.
    /// A concurrent <see cref="GenerateAsync"/> that has already acquired the internal
    /// semaphore will complete before disposal proceeds; a call that has not yet acquired it
    /// may observe <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        // Acquire the semaphore before touching native handles so we never free them
        // while GenerateAsync holds the lock and is mid-inference.  A brief wait is
        // acceptable: this is a long-lived singleton disposed only at shutdown.
        // If the semaphore itself is already disposed (double-Dispose) we exit silently.
        try
        {
            _lock.Wait();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _embedder?.Dispose();
            _weights?.Dispose();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
