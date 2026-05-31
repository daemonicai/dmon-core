using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dmon.Abstractions;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Pipeline;
using Dmon.Core.Profiles;
using Dmon.Core.Providers;
using Dmon.Core.Session;
using Dmon.Core.Telemetry;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Delta;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Rpc;

public sealed class TurnHandler : ITurnHandler
{
    private readonly IProviderRegistry _providers;
    private readonly IActiveModelStore _activeModelStore;
    private readonly IToolRegistry _tools;
    private readonly IEventEmitter _emitter;
    private readonly IPermissionPolicy _policy;
    private readonly IThinkingHandler _thinking;
    private readonly ISessionHandler _sessionHandler;
    private readonly IAttachmentStore _attachmentStore;
    private readonly ISystemPromptBuilder _systemPromptBuilder;
    private readonly MiddlewarePipelineBuilder _pipelineBuilder;
    private readonly RetryPolicy _retryPolicy;
    private readonly IAgentProfileResolver _profileResolver;
    private readonly AgentProfileContext _profileContext;
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
    private bool _systemPromptInjected;

    public TurnHandler(
        IProviderRegistry providers,
        IActiveModelStore activeModelStore,
        IToolRegistry tools,
        IEventEmitter emitter,
        IPermissionPolicy policy,
        IThinkingHandler thinking,
        ISessionHandler sessionHandler,
        IAttachmentStore attachmentStore,
        ISystemPromptBuilder systemPromptBuilder,
        MiddlewarePipelineBuilder pipelineBuilder,
        IConfiguration configuration,
        IAgentProfileResolver profileResolver,
        AgentProfileContext profileContext,
        ILogger<TurnHandler> logger)
    {
        _providers = providers;
        _activeModelStore = activeModelStore;
        _tools = tools;
        _emitter = emitter;
        _policy = policy;
        _thinking = thinking;
        _sessionHandler = sessionHandler;
        _attachmentStore = attachmentStore;
        _systemPromptBuilder = systemPromptBuilder;
        _pipelineBuilder = pipelineBuilder;
        _retryPolicy = RetryPolicy.FromConfiguration(configuration);
        _profileResolver = profileResolver;
        _profileContext = profileContext;
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
            if (!_systemPromptInjected)
            {
                // Resolve the agent profile once per session before building the system prompt.
                // requestedProfile is null until the RPC protocol wires per-session profile selection
                // (later in this change); null falls back to defaultProfile or built-in coding.
                await _profileContext.EnsureResolvedAsync(_profileResolver, requestedProfile: null, _turnCts.Token)
                    .ConfigureAwait(false);

                ChatMessage systemMessage = await _systemPromptBuilder.BuildAsync(_turnCts.Token).ConfigureAwait(false);
                _history.Insert(0, systemMessage);
                _systemPromptInjected = true;
            }

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
        using Activity? turnActivity = DmonTelemetry.Source.StartActivity("turn");

        string stopReason = "completed";
        // Intentionally captured before CommitPendingSwitch — these reflect the pre-switch provider/model
        // for this turn's telemetry; reconciling telemetry across mid-turn switches is deferred.
        string provider = _providers.GetCurrentConfig().Name;
        string model = _providers.GetCurrentConfig().DefaultModelId ?? "unknown";
        string thinkingLevel = _thinking.CurrentLevel.ToString();
        long inputTokens = 0;
        long outputTokens = 0;

        if (turnActivity is not null)
        {
            turnActivity.SetTag("dmon.provider", provider);
            turnActivity.SetTag("dmon.model", model);
            turnActivity.SetTag("dmon.thinking.level", thinkingLevel);
        }

        Stopwatch turnTimer = Stopwatch.StartNew();

        // Commit any pending provider switch queued between turns, before the turn resolves its
        // client. A switch queued during an in-flight turn is committed at the start of the
        // following turn (it arrives after this point and is picked up next time RunTurnAsync runs).
        // Use CancellationToken.None — these emits must reach the host even when the prior turn aborted.
        ProviderSwitchResult? switchResult = _providers.CommitPendingSwitch();
        if (switchResult is not null)
        {
            string? savedModelId = string.IsNullOrEmpty(switchResult.ModelId) ? null : switchResult.ModelId;
            await _activeModelStore.SaveAsync(
                new ModelRef(switchResult.ProviderName, savedModelId),
                CancellationToken.None).ConfigureAwait(false);

            await _emitter.EmitAsync(new ProviderSwitchedEvent
            {
                Name = switchResult.ProviderName,
                Model = switchResult.ModelId,
                EffectiveNextTurn = false
            }, CancellationToken.None).ConfigureAwait(false);
        }

        await _emitter.EmitAsync(new TurnStartEvent(), cancellationToken).ConfigureAwait(false);

        List<object> toolResults = [];
        object lastMessage = new { };

        // Iterate rather than recurse to avoid unbounded stack growth from follow-up chains.
        while (true)
        {
            // Build the pipeline per-turn so provider switches take effect immediately.
            IChatClient providerClient = await _providers.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            // Fold user middleware over the raw provider client. Instances live for the process
            // lifetime (D6 — no hot-reload); only the Wrap call repeats each turn so that the
            // fresh providerClient (rebuilt on model/provider switch) is always the innermost layer.
            providerClient = _pipelineBuilder.Apply(providerClient);
            IChatClient retrying = new RetryingChatClient(providerClient, _retryPolicy, _emitter, provider, model);
            IChatClient offloading = new AttachmentOffloadingChatClient(retrying, _sessionHandler, _attachmentStore);
            IChatClient functionInvoker = new FunctionInvokingChatClient(offloading);
            IChatClient pipeline = new PermissionGateChatClient(functionInvoker, _policy, _tools, ConfirmCallback, _profileContext, _sessionHandler);

            IReadOnlyList<AIFunction> toolList = _tools.GetAll();
            ChatOptions options = new();
            if (_providers.CurrentSupportsToolCalling && toolList.Count > 0)
            {
                options.Tools = toolList.Cast<AITool>().ToList();
            }

            ApplyThinkingToOptions(options);

            string? activeModelId = _providers.GetCurrentModelId() ?? _providers.GetCurrentConfig().DefaultModelId;
            if (!string.IsNullOrWhiteSpace(activeModelId))
                options.ModelId = activeModelId;

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
                stopReason = "cancelled";
            }

            break;
        }

        // --- Record telemetry ---
        turnTimer.Stop();

        if (turnActivity is not null)
        {
            turnActivity.SetTag("dmon.stop_reason", stopReason);
            turnActivity.SetTag("dmon.tokens.input", inputTokens);
            turnActivity.SetTag("dmon.tokens.output", outputTokens);
        }

        DmonTelemetry.RecordTurn(provider, model, stopReason);
        DmonTelemetry.RecordTurnDuration(turnTimer.Elapsed.TotalMilliseconds, provider, model, stopReason);

        await _emitter.EmitAsync(new TurnEndEvent
        {
            Message = lastMessage,
            ToolResults = toolResults
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void ApplyThinkingToOptions(ChatOptions options)
    {
        ThinkingLevel level = _thinking.CurrentLevel;
        if (level == ThinkingLevel.Off)
        {
            return;
        }

        // Anthropic: budget_tokens in AdditionalProperties.
        int budgetTokens = level switch
        {
            ThinkingLevel.Low => 1024,
            ThinkingLevel.Medium => 8192,
            ThinkingLevel.High => 32768,
            _ => 0
        };

        string providerName = _providers.GetCurrentConfig().Name;

        if (string.Equals(providerName, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["thinking"] = new { type = "enabled", budget_tokens = budgetTokens };
        }
        else if (string.Equals(providerName, "openai", StringComparison.OrdinalIgnoreCase))
        {
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["reasoning_effort"] = level switch
            {
                ThinkingLevel.Low => "low",
                ThinkingLevel.Medium => "medium",
                ThinkingLevel.High => "high",
                _ => null
            };
        }
        else
        {
            // Gemini and unknown providers: store budget in AdditionalProperties.
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties["thinkingBudget"] = budgetTokens;
        }
    }

    private async Task HandleToolCallAsync(
        FunctionCallContent call,
        List<object> toolResults,
        CancellationToken cancellationToken)
    {
        using Activity? toolActivity = DmonTelemetry.Source.StartActivity("tool.execute");

        int argsSizeBytes = JsonSerializer.SerializeToUtf8Bytes(call.Arguments ?? new Dictionary<string, object?>()).Length;

        if (toolActivity is not null)
        {
            toolActivity.SetTag("dmon.tool.name", call.Name);
            toolActivity.SetTag("dmon.tool.args.size_bytes", argsSizeBytes);
        }

        await _emitter.EmitAsync(new ToolExecutionStartEvent
        {
            CallId = call.CallId,
            Name = call.Name,
            Args = call.Arguments ?? new Dictionary<string, object?>()
        }, cancellationToken).ConfigureAwait(false);

        // TODO(Group 9.5): Tool results are obtained from FunctionInvokingChatClient in the pipeline.
        object result = new { callId = call.CallId, name = call.Name };
        bool isError = false;

        int resultSizeBytes = JsonSerializer.SerializeToUtf8Bytes(result).Length;

        if (toolActivity is not null)
        {
            toolActivity.SetTag("dmon.tool.result.size_bytes", resultSizeBytes);
            toolActivity.SetTag("dmon.tool.is_error", isError);
        }

        await _emitter.EmitAsync(new ToolExecutionEndEvent
        {
            CallId = call.CallId,
            Result = result,
            IsError = isError
        }, cancellationToken).ConfigureAwait(false);

        toolResults.Add(result);

        DmonTelemetry.RecordToolInvocation(call.Name, isError);
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


