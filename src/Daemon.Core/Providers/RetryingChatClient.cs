using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Daemon.Core.Rpc;
using Daemon.Core.Telemetry;
using Daemon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Providers;

/// <summary>
/// IChatClient middleware that retries on transient provider errors with exponential backoff and jitter.
/// Non-retryable errors (4xx except 408/429, auth failures) propagate immediately.
/// </summary>
public sealed class RetryingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly RetryPolicy _policy;
    private readonly IEventEmitter _emitter;
    private readonly string _providerName;
    private readonly string _modelName;

    public RetryingChatClient(
        IChatClient inner,
        RetryPolicy policy,
        IEventEmitter emitter,
        string providerName = "unknown",
        string modelName = "unknown")
    {
        _inner = inner;
        _policy = policy;
        _emitter = emitter;
        _providerName = providerName;
        _modelName = modelName;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();

        for (int attempt = 0; attempt < _policy.MaxAttempts; attempt++)
        {
            using Activity? callActivity = DaemonTelemetry.Source.StartActivity("provider.call");
            if (callActivity is not null)
            {
                callActivity.SetTag("daemon.provider", _providerName);
                callActivity.SetTag("daemon.model", _modelName);
                callActivity.SetTag("daemon.retry.attempt", attempt);
            }

            try
            {
                return await _inner.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                lastException = ex;

                if (callActivity is not null)
                {
                    callActivity.SetStatus(ActivityStatusCode.Error, ex.Message);
                }

                DaemonTelemetry.RecordProviderRetry(_providerName, ex.Message);

                // On the final attempt, do not emit a retry event or sleep — there is no further retry.
                if (attempt == _policy.MaxAttempts - 1)
                {
                    break;
                }

                TimeSpan delay = ComputeDelay(attempt, ex);

                await _emitter.EmitAsync(new RetryAttemptEvent
                {
                    Attempt = attempt + 1,
                    MaxAttempts = _policy.MaxAttempts,
                    NextDelayMs = (int)delay.TotalMilliseconds,
                    Reason = ex.Message
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException!;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();
        int attempt = 0;

        while (true)
        {
            using Activity? callActivity = DaemonTelemetry.Source.StartActivity("provider.call");
            if (callActivity is not null)
            {
                callActivity.SetTag("daemon.provider", _providerName);
                callActivity.SetTag("daemon.model", _modelName);
                callActivity.SetTag("daemon.retry.attempt", attempt);
            }
            // Use a channel so we can yield updates without a try/catch around the yield statement
            // (which the C# compiler forbids). The producer task runs the inner stream and signals
            // whether it started before any exception, so we know whether to retry.
            System.Threading.Channels.Channel<ChatResponseUpdate> channel =
                System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();

            // streamStarted is set true by the producer after the first item is written.
            bool streamStarted = false;
            Exception? producerException = null;

            Task producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (ChatResponseUpdate update in
                        _inner.GetStreamingResponseAsync(messageList, options, cancellationToken)
                              .ConfigureAwait(false))
                    {
                        streamStarted = true;
                        await channel.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    producerException = ex;
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            // Yield items as they arrive — no buffering, no try/catch around yield.
            await foreach (ChatResponseUpdate update in
                channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            await producer.ConfigureAwait(false);

            if (producerException is null)
            {
                // Stream completed successfully.
                yield break;
            }

            // Stream failed. Only retry if it failed before the first item and is retryable.
            if (streamStarted || !IsRetryable(producerException) || attempt >= _policy.MaxAttempts - 1)
            {
                // Set error status on the span before throwing.
                if (callActivity is not null && producerException is not null)
                {
                    callActivity.SetStatus(ActivityStatusCode.Error, producerException.Message);
                }
                // Mid-stream failure, non-retryable error, or out of attempts — propagate.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerException!).Throw();
                yield break; // unreachable, satisfies compiler
            }

            if (callActivity is not null && producerException is not null)
            {
                callActivity.SetStatus(ActivityStatusCode.Error, producerException.Message);
            }

            DaemonTelemetry.RecordProviderRetry(_providerName, producerException?.Message ?? "unknown");

            TimeSpan delay = ComputeDelay(attempt, producerException!);
            attempt++;

            await _emitter.EmitAsync(new RetryAttemptEvent
            {
                Attempt = attempt,
                MaxAttempts = _policy.MaxAttempts,
                NextDelayMs = (int)delay.TotalMilliseconds,
                Reason = producerException!.Message
            }, cancellationToken).ConfigureAwait(false);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan ComputeDelay(int attempt, Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            TimeSpan? retryAfter = ExtractRetryAfter(httpEx);
            if (retryAfter.HasValue)
            {
                return retryAfter.Value;
            }
        }

        double exponential = _policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        double capped = Math.Min(_policy.MaxDelay.TotalMilliseconds, exponential);
        double jitter = capped * 0.25 * (2.0 * Random.Shared.NextDouble() - 1.0);
        return TimeSpan.FromMilliseconds(Math.Max(0, capped + jitter));
    }

    private static TimeSpan? ExtractRetryAfter(HttpRequestException ex)
    {
        // Provider SDKs that wish to surface Retry-After should populate
        // exception.Data["Retry-After"] with an int (seconds), string (parseable int), or TimeSpan.
        if (!ex.Data.Contains("Retry-After"))
            return null;

        object? value = ex.Data["Retry-After"];
        if (value is int seconds)
            return TimeSpan.FromSeconds(seconds);
        if (value is string s && int.TryParse(s, out int parsed))
            return TimeSpan.FromSeconds(parsed);
        if (value is TimeSpan ts)
            return ts;

        return null;
    }

    private static bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is HttpRequestException httpEx)
        {
            HttpStatusCode? status = httpEx.StatusCode;
            if (status is null)
            {
                return true;
            }

            int code = (int)status;

            if (code == 429 || code == 408)
            {
                return true;
            }

            if (code >= 500)
            {
                return true;
            }

            return false;
        }

        string message = ex.Message;
        if (message.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public void Dispose() => _inner.Dispose();
}
