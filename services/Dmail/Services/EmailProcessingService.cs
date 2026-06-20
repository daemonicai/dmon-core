using System.Threading.Channels;
using Dmail.Models;

namespace Dmail.Services;

public sealed class EmailProcessingService : BackgroundService
{
    private readonly ChannelReader<Email> _channel;
    private readonly EmbeddingService _embedding;
    private readonly EmailRepository _emailRepo;
    private readonly VectorStoreService _vectorStore;
    private readonly ILogger<EmailProcessingService> _logger;

    public EmailProcessingService(
        Channel<Email> channel,
        EmbeddingService embedding,
        EmailRepository emailRepo,
        VectorStoreService vectorStore,
        ILogger<EmailProcessingService> logger)
    {
        _channel = channel.Reader;
        _embedding = embedding;
        _emailRepo = emailRepo;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Email processing service started");

        // Task 4.6: First, retry any pending embeddings from previous failures
        await RetryPendingEmbeddingsAsync(stoppingToken);

        await foreach (var email in _channel.ReadAllAsync(stoppingToken))
        {
            await ProcessEmailAsync(email, stoppingToken);
        }
    }

    private async Task ProcessEmailAsync(Email email, CancellationToken ct)
    {
        try
        {
            if (!_embedding.IsModelReady)
            {
                _logger.LogWarning("Model not ready — marking email {Uid} as pending embedding", email.Uid);
                // Persist the email so it survives restart and is retried; mark pending.
                email.PendingEmbedding = true;
                await _emailRepo.UpsertEmailAsync(email, ct);
                return;
            }

            // Commit the base row first with pending_embedding = 1 (dedup by account+uid).
            // We deliberately do NOT hold a DB transaction across ONNX inference. If the
            // process crashes after this point, the row stays pending and is reprocessed
            // on restart via RetryPendingEmbeddingsAsync.
            email.PendingEmbedding = true;
            await _emailRepo.UpsertEmailAsync(email, ct);

            // Task 6.2: Generate embedding
            var text = EmbeddingService.BuildEmbeddingText(email.Subject, email.Body);
            var vector = await _embedding.GenerateEmbeddingAsync(text, ct);

            // Task 6.4: Store embedding vector via SK SqliteCollection, then clear the
            // pending flag. Writing the vector before clearing pending guarantees a crash
            // can never leave a vectorless-but-done email.
            await _vectorStore.UpsertVectorAsync(email.Account, email.Uid, vector, ct);
            await _emailRepo.ClearPendingEmbeddingAsync(email.Account, email.Uid, ct);
            email.PendingEmbedding = false;
            await _emailRepo.UpdateAccountIndexStatusAsync(email.Account, ct);

            _logger.LogDebug("Processed email {Uid} for {Account}", email.Uid, email.Account);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process email {Uid} for {Account}", email.Uid, email.Account);
            // Mark as pending so it's retried on recovery. The base row was already
            // committed with pending=1 above, so this is a safety net.
            await _emailRepo.MarkPendingEmbeddingAsync(email.Account, email.Uid, ct);
        }
    }

    private async Task RetryPendingEmbeddingsAsync(CancellationToken ct)
    {
        if (!_embedding.IsModelReady) return;

        var pending = await _emailRepo.GetPendingEmbeddingsAsync(50);
        if (pending.Count == 0) return;

        _logger.LogInformation("Retrying {Count} pending embeddings", pending.Count);

        var texts = pending.Select(e =>
            EmbeddingService.BuildEmbeddingText(e.Subject, e.Body)).ToArray();

        try
        {
            var vectors = await _embedding.GenerateBatchEmbeddingsAsync(texts, ct);
            var entries = new List<(string, uint, float[])>();
            for (int i = 0; i < pending.Count; i++)
            {
                entries.Add((pending[i].Account, pending[i].Uid, vectors[i]));
            }

            // Task 6.4: Write vectors first, then clear the pending flag — so an
            // interrupted retry leaves rows pending (reprocessable) rather than
            // marked-done-without-a-vector.
            await _vectorStore.UpsertBatchAsync(entries, ct);
            foreach (var e in pending)
            {
                await _emailRepo.ClearPendingEmbeddingAsync(e.Account, e.Uid, ct);
                e.PendingEmbedding = false;
                await _emailRepo.UpdateAccountIndexStatusAsync(e.Account, ct);
            }
            _logger.LogInformation("Successfully retried {Count} pending embeddings", pending.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry pending embeddings");
        }
    }
}
