using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Daemon.Core.Extensions;
using Daemon.Core.Permissions;
using Daemon.Core.Providers;
using Daemon.Protocol.Commands;
using Daemon.Protocol.Delta;
using Daemon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daemon.Core.Rpc;

public sealed class TurnHandler : ITurnHandler
{
    private readonly IProviderRegistry _providers;
    private readonly IToolRegistry _tools;
    private readonly IEventEmitter _emitter;
    private readonly IPermissionPolicy _policy;
    private readonly ILogger<TurnHandler> _logger;

    // Pending confirm/ui-input response channels keyed by request id.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingConfirms = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UiInputResult>> _pendingUiInputs = new();

    // Turn state — only one turn active at a time.
    private volatile CancellationTokenSource? _turnCts;
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    // Queued steer/followUp messages received during an active turn.
    private string? _pendingSteer;
    private string? _pendingFollowUp;

    // Conversation history for this session.
    private readonly List<ChatMessage> _history = [];

    public TurnHandler(
        IProviderRegistry providers,
        IToolRegistry tools,
        IEventEmitter emitter,
        IPermissionPolicy policy,
        ILogger<TurnHandler> logger)
    {
        _providers = providers;
        _tools = tools;
        _emitter = emitter;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Returns the confirm callback suitable for wiring into <see cref="PermissionGateChatClient"/>.
    /// </summary>
    public Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> ConfirmCallback => HandleConfirmRequestAsync;

    public async Task SubmitAsync(TurnSubmitCommand cmd, CancellationToken cancellationToken)
    {
        if (!await _turnGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "turnInProgress",
                Message = "A turn is already in progress.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            ChatMessage userMessage = new(ChatRole.User, cmd.Message);
            _history.Add(userMessage);
            await RunTurnAsync(_turnCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _turnCts.Dispose();
            _turnCts = null;
            Volatile.Write(ref _pendingSteer, null);
            Volatile.Write(ref _pendingFollowUp, null);
            _turnGate.Release();
        }
    }

    public Task SteerAsync(TurnSteerCommand cmd, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _pendingSteer, cmd.Message);
        return Task.CompletedTask;
    }

    public Task FollowUpAsync(TurnFollowUpCommand cmd, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _pendingFollowUp, cmd.Message);
        return Task.CompletedTask;
    }

    public async Task AbortAsync(TurnAbortCommand cmd, CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = _turnCts;
        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task ConfirmResponseAsync(ToolConfirmResponseCommand cmd, CancellationToken cancellationToken)
    {
        if (_pendingConfirms.TryRemove(cmd.Id, out TaskCompletionSource<bool>? tcs))
        {
            if (cmd.Cancelled)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                tcs.TrySetResult(cmd.Confirmed);
            }
        }
        else
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "unknownConfirmId",
                Message = $"No pending confirm request with id '{cmd.Id}'.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UiInputResponseAsync(UiInputResponseCommand cmd, CancellationToken cancellationToken)
    {
        if (_pendingUiInputs.TryRemove(cmd.Id, out TaskCompletionSource<UiInputResult>? tcs))
        {
            if (cmd.Cancelled)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            else
            {
                tcs.TrySetResult(new UiInputResult(cmd.Value));
            }
        }
        else
        {
            await _emitter.EmitAsync(new ErrorEvent
            {
                Code = "unknownUiInputId",
                Message = $"No pending ui.inputRequest with id '{cmd.Id}'.",
                Recoverable = true
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunTurnAsync(CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new TurnStartEvent(), cancellationToken).ConfigureAwait(false);

        List<object> toolResults = [];
        object lastMessage = new { };

        // Iterate rather than recurse to avoid unbounded stack growth from follow-up chains.
        while (true)
        {
            // Build the pipeline per-turn so provider switches take effect immediately.
            IChatClient providerClient = await _providers.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            IChatClient functionInvoker = new FunctionInvokingChatClient(providerClient);
            IChatClient pipeline = new PermissionGateChatClient(functionInvoker, _policy, ConfirmCallback);

            IReadOnlyList<AIFunction> toolList = _tools.GetAll();
            ChatOptions options = new();
            if (_providers.CurrentSupportsToolCalling && toolList.Count > 0)
            {
                options.Tools = toolList.Cast<AITool>().ToList();
            }

            try
            {
                // Apply any pending steer before the LLM call.
                string? steer = Volatile.Read(ref _pendingSteer);
                if (steer is not null)
                {
                    _history.Add(new ChatMessage(ChatRole.User, steer));
                    Volatile.Write(ref _pendingSteer, null);
                }

                StringBuilder accumulatedText = new();

                await _emitter.EmitAsync(new MessageStartEvent { Message = new { } }, cancellationToken)
                    .ConfigureAwait(false);

                // Emit turn start delta.
                await _emitter.EmitAsync(new MessageDeltaEvent
                {
                    Message = new { },
                    Delta = new StartDelta()
                }, cancellationToken).ConfigureAwait(false);

                // Emit textStart before the first text chunk.
                bool textStarted = false;

                await foreach (ChatResponseUpdate update in
                    pipeline.GetStreamingResponseAsync(_history, options, cancellationToken).ConfigureAwait(false))
                {
                    foreach (AIContent content in update.Contents)
                    {
                        if (content is TextContent text)
                        {
                            if (!textStarted)
                            {
                                textStarted = true;
                                await _emitter.EmitAsync(new MessageDeltaEvent
                                {
                                    Message = new { },
                                    Delta = new TextStartDelta()
                                }, cancellationToken).ConfigureAwait(false);
                            }

                            accumulatedText.Append(text.Text);
                            await _emitter.EmitAsync(new MessageDeltaEvent
                            {
                                Message = new { },
                                Delta = new TextDeltaDelta { Delta = text.Text, Partial = true }
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        else if (content is FunctionCallContent call)
                        {
                            await HandleToolCallAsync(call, toolResults, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }

                string fullText = accumulatedText.ToString();

                if (textStarted)
                {
                    await _emitter.EmitAsync(new MessageDeltaEvent
                    {
                        Message = new { },
                        Delta = new TextEndDelta { Content = fullText }
                    }, cancellationToken).ConfigureAwait(false);
                }

                lastMessage = new { role = "assistant", content = fullText };

                await _emitter.EmitAsync(new MessageEndEvent { Message = lastMessage }, cancellationToken)
                    .ConfigureAwait(false);

                _history.Add(new ChatMessage(ChatRole.Assistant, fullText));

                // Apply any queued follow-up as an additional iteration.
                string? followUp = Volatile.Read(ref _pendingFollowUp);
                if (followUp is not null)
                {
                    Volatile.Write(ref _pendingFollowUp, null);
                    _history.Add(new ChatMessage(ChatRole.User, followUp));
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Turn cancelled.");
            }

            break;
        }

        // Commit any pending provider switch between turns.
        ProviderSwitchedEvent? switched = _providers.CommitPendingSwitch();
        if (switched is not null)
        {
            await _emitter.EmitAsync(switched, cancellationToken).ConfigureAwait(false);
        }

        await _emitter.EmitAsync(new TurnEndEvent
        {
            Message = lastMessage,
            ToolResults = toolResults
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleToolCallAsync(
        FunctionCallContent call,
        List<object> toolResults,
        CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new ToolExecutionStartEvent
        {
            CallId = call.CallId,
            Name = call.Name,
            Args = call.Arguments ?? new Dictionary<string, object?>()
        }, cancellationToken).ConfigureAwait(false);

        // TODO(Group 9.5): Tool results are obtained from FunctionInvokingChatClient in the pipeline.
        object result = new { callId = call.CallId, name = call.Name };
        bool isError = false;

        await _emitter.EmitAsync(new ToolExecutionEndEvent
        {
            CallId = call.CallId,
            Result = result,
            IsError = isError
        }, cancellationToken).ConfigureAwait(false);

        toolResults.Add(result);
    }

    private async Task<bool> HandleConfirmRequestAsync(
        ToolConfirmRequestEvent evt,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirms[evt.ConfirmId] = tcs;

        await _emitter.EmitAsync(evt, cancellationToken).ConfigureAwait(false);

        using CancellationTokenRegistration reg = cancellationToken.Register(
            () => tcs.TrySetCanceled(cancellationToken));

        return await tcs.Task.ConfigureAwait(false);
    }
}


