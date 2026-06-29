using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dmon.Abstractions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Providers;
using Dmon.Core.Session;
using Dmon.Core.Telemetry;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
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
    private readonly ISessionStore? _sessionStore;
    private readonly ISystemPromptBuilder _systemPromptBuilder;
    private readonly MiddlewarePipelineBuilder _pipelineBuilder;
    private readonly RetryPolicy _retryPolicy;
    private readonly ISessionAssetProvisioner _assetProvisioner;
    private readonly AssetsOptions? _assetsOptions;
    private readonly PermissionModeOptions? _permissionModeOptions;
    private readonly ILogger<TurnHandler> _logger;
    private readonly ITerminalClientFactory? _terminalClientFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IEnumerable<ISessionActivityListener> _activityListeners;

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

    // Tracks how many _history entries have been persisted; only new entries are written each turn.
    private int _persistedCount;

    public TurnHandler(
        IProviderRegistry providers,
        IActiveModelStore activeModelStore,
        IToolRegistry tools,
        IEventEmitter emitter,
        IPermissionPolicy policy,
        IThinkingHandler thinking,
        ISessionHandler sessionHandler,
        ISystemPromptBuilder systemPromptBuilder,
        MiddlewarePipelineBuilder pipelineBuilder,
        IConfiguration configuration,
        ISessionAssetProvisioner assetProvisioner,
        ILogger<TurnHandler> logger,
        AssetsOptions? assetsOptions = null,
        PermissionModeOptions? permissionModeOptions = null,
        ISessionStore? sessionStore = null,
        ITerminalClientFactory? terminalClientFactory = null,
        IServiceProvider? serviceProvider = null,
        IEnumerable<ISessionActivityListener>? activityListeners = null)
    {
        _providers = providers;
        _activeModelStore = activeModelStore;
        _tools = tools;
        _emitter = emitter;
        _policy = policy;
        _thinking = thinking;
        _sessionHandler = sessionHandler;
        _sessionStore = sessionStore;
        _systemPromptBuilder = systemPromptBuilder;
        _pipelineBuilder = pipelineBuilder;
        _retryPolicy = RetryPolicy.FromConfiguration(configuration);
        _assetProvisioner = assetProvisioner;
        _assetsOptions = assetsOptions;
        _permissionModeOptions = permissionModeOptions;
        _logger = logger;
        _terminalClientFactory = terminalClientFactory;
        _serviceProvider = serviceProvider;
        _activityListeners = activityListeners ?? [];

        if (terminalClientFactory is not null && serviceProvider is null)
            throw new ArgumentNullException(nameof(serviceProvider),
                "An ITerminalClientFactory requires an IServiceProvider to resolve backends.");
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
            NotifyTurnStarted(_sessionHandler.CurrentSession?.Id);

            if (!_systemPromptInjected)
            {
                // Provision assets/<session_id>/ under the workspace root when UseAssets was called.
                // Must run before BuildAsync so the system-prompt asset-dir line refers to a
                // directory that already exists on disk.
                _assetProvisioner.Provision(
                    _assetsOptions is not null,
                    _assetsOptions?.Path,
                    _sessionHandler.CurrentSession?.Id);

                ChatMessage systemMessage = await _systemPromptBuilder.BuildAsync(_turnCts.Token).ConfigureAwait(false);
                _history.Insert(0, systemMessage);
                _systemPromptInjected = true;
                // The Insert shifts all existing entries up by 1. Advance _persistedCount to
                // keep it pointing past the already-persisted seeded entries.
                _persistedCount++;
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

    private void NotifyTurnStarted(string? sessionId)
    {
        if (sessionId is null)
            return;
        foreach (ISessionActivityListener listener in _activityListeners)
        {
            try
            {
                listener.OnTurnStarted(sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ISessionActivityListener.OnTurnStarted threw for session {SessionId}.", sessionId);
            }
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
            // If a terminal-client factory is registered, it supplies the base client; otherwise
            // fall through to the provider-registry active provider (no-factory path unchanged).
            IChatClient providerClient = _terminalClientFactory is not null
                ? _terminalClientFactory.Create(_serviceProvider!)
                : await _providers.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            // Fold user middleware over the raw provider client. Instances live for the process
            // lifetime (D6 — no hot-reload); only the Wrap call repeats each turn so that the
            // fresh providerClient (rebuilt on model/provider switch) is always the innermost layer.
            providerClient = _pipelineBuilder.Apply(providerClient);
            IChatClient retrying = new RetryingChatClient(providerClient, _retryPolicy, _emitter, provider, model);
            IChatClient functionInvoker = new FunctionInvokingChatClient(retrying);
            IChatClient pipeline = new PermissionGateChatClient(
                functionInvoker, _policy, _tools, ConfirmCallback, _sessionHandler,
                _assetsOptions, _permissionModeOptions);

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

            // Declared outside try so the catch block can run the anomaly sweep on abort.
            Dictionary<string, FunctionCallContent> accumulatedCalls = [];
            List<string> callOrder = [];
            Dictionary<string, FunctionResultContent> accumulatedResults = [];
            HashSet<string> startedCallIds = [];
            HashSet<string> endedCallIds = [];

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
                            string callId = call.CallId ?? string.Empty;

                            // Track insertion order; only record the first time a callId is seen.
                            if (!accumulatedCalls.ContainsKey(callId))
                                callOrder.Add(callId);

                            // Last-write-wins: later updates carry more-complete Name/Arguments.
                            accumulatedCalls[callId] = call;

                            // Fire toolExecutionStart exactly once per distinct callId.
                            if (startedCallIds.Add(callId))
                            {
                                await EmitToolExecutionStartAsync(call, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        else if (content is FunctionResultContent result)
                        {
                            string resultCallId = result.CallId ?? string.Empty;
                            // Last-write-wins for results too.
                            accumulatedResults[resultCallId] = result;

                            // Emit toolExecutionEnd exactly once per callId as results arrive.
                            if (endedCallIds.Add(resultCallId))
                            {
                                int? argsSizeBytes = accumulatedCalls.TryGetValue(resultCallId, out FunctionCallContent? matchedCall)
                                    ? JsonSerializer.SerializeToUtf8Bytes(matchedCall.Arguments ?? new Dictionary<string, object?>()).Length
                                    : null;
                                await EmitToolExecutionEndAsync(
                                    resultCallId,
                                    matchedCall?.Name ?? resultCallId,
                                    result,
                                    toolResults,
                                    argsSizeBytes,
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }

                // Anomaly sweep: any call that got a toolExecutionStart but no observed
                // FunctionResultContent (provider delivered no result before stream end) gets
                // a toolExecutionEnd with an error marker so the host UI never hangs.
                // Currently shadowed by PermissionGateChatClient's full-buffering; exists as the
                // D3 guarantee for a future non-buffering middleware that yields items incrementally.
                await EmitMissingToolEndEventsAsync(
                    startedCallIds, endedCallIds, accumulatedCalls, toolResults,
                    CancellationToken.None).ConfigureAwait(false);

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

                // Build the canonical assistant message: text first, then function calls in
                // the order they were first observed in the stream.
                List<AIContent> assistantContents = [];
                if (fullText.Length > 0)
                    assistantContents.Add(new TextContent(fullText));
                foreach (string callId in callOrder)
                    assistantContents.Add(accumulatedCalls[callId]);
                _history.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                // Append the tool-role message only when at least one result was captured.
                if (accumulatedResults.Count > 0)
                {
                    List<AIContent> toolContents = [];
                    // Emit results in the same order as their corresponding calls.
                    foreach (string callId in callOrder)
                    {
                        if (accumulatedResults.TryGetValue(callId, out FunctionResultContent? resultContent))
                            toolContents.Add(resultContent);
                    }
                    // Any results whose callId had no matching call go at the end.
                    foreach (KeyValuePair<string, FunctionResultContent> kv in accumulatedResults)
                    {
                        if (!accumulatedCalls.ContainsKey(kv.Key))
                            toolContents.Add(kv.Value);
                    }
                    _history.Add(new ChatMessage(ChatRole.Tool, toolContents));
                }

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

                // On abort: emit error-marker ends for any calls that started but never resolved.
                await EmitMissingToolEndEventsAsync(
                    startedCallIds, endedCallIds, accumulatedCalls, toolResults,
                    CancellationToken.None).ConfigureAwait(false);
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

        // Persist the new _history entries added during this turn (best-effort).
        await PersistNewHistoryEntriesAsync(CancellationToken.None).ConfigureAwait(false);

        await _emitter.EmitAsync(new TurnEndEvent
        {
            Message = lastMessage,
            ToolResults = toolResults
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistNewHistoryEntriesAsync(CancellationToken cancellationToken)
    {
        if (_sessionStore is null || _sessionHandler.CurrentSession is null)
            return;

        string sessionId = _sessionHandler.CurrentSession.Id;

        // Collect the _history indices of non-system entries added since the last persist.
        // We need the indices (not just a copy) so we can splice the preview form back in place.
        List<int> newIndices = [];
        for (int i = _persistedCount; i < _history.Count; i++)
        {
            if (_history[i].Role != ChatRole.System)
                newIndices.Add(i);
        }

        if (newIndices.Count == 0)
        {
            _persistedCount = _history.Count;
            return;
        }

        List<ChatMessage> newEntries = newIndices.Select(i => _history[i]).ToList();

        try
        {
            IReadOnlyList<MessageRecord> written = await _sessionStore.AppendMessagesAsync(
                sessionId, newEntries, cancellationToken: cancellationToken).ConfigureAwait(false);

            // D6 reconciliation: replace the just-persisted history entries with their persisted
            // preview form (attachment refs resolved, offloaded results truncated to null).
            // This ensures subsequent turns send the shrunk form rather than the full in-memory content.
            // written is in 1:1 correspondence with newIndices (AppendMessagesAsync skips system, which
            // we already filtered out above).
            int writeIdx = 0;
            foreach (int historyIdx in newIndices)
            {
                if (writeIdx < written.Count)
                {
                    ChatMessage spliced = ConversationMapper.ToMessage(written[writeIdx]);
                    // Mirror the empty-content skip from SeedHistoryFromSessionAsync: if the
                    // record maps to zero replay-subset contents (e.g. an offloaded-only turn),
                    // leave the in-memory entry unchanged rather than replacing it with an empty
                    // ChatMessage that some providers reject.
                    if (spliced.Contents.Count > 0)
                        _history[historyIdx] = spliced;
                    writeIdx++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turn persistence failed for session {SessionId} — turn completed but not persisted.", sessionId);
        }
        finally
        {
            _persistedCount = _history.Count;
        }
    }

    public async Task SeedHistoryFromSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessionStore is null)
            return;

        IReadOnlyList<SessionLogLine> records = await _sessionStore
            .ReadRecordsAsync(sessionId, applyCompaction: true, cancellationToken)
            .ConfigureAwait(false);

        _history.Clear();
        _systemPromptInjected = false;
        _persistedCount = 0;

        foreach (SessionLogLine line in records)
        {
            switch (line)
            {
                case MessageRecord mr:
                {
                    ChatMessage message = ConversationMapper.ToMessage(mr);
                    // Skip messages that map to empty content (e.g. offloaded-only assistant turns).
                    if (message.Contents.Count > 0)
                        _history.Add(message);
                    break;
                }
                case CompactionMessage cm:
                    // The compaction summary stands in for all superseded turns.
                    // Seed it as a synthetic assistant message so the model has context about
                    // what happened before the compaction boundary.
                    _history.Add(new ChatMessage(ChatRole.Assistant, cm.Summary));
                    break;
            }
        }

        // All seeded entries are already persisted; mark them so the next turn doesn't re-append.
        _persistedCount = _history.Count;
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

    private async Task EmitToolExecutionStartAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new ToolExecutionStartEvent
        {
            CallId = call.CallId,
            Name = call.Name,
            Args = call.Arguments ?? new Dictionary<string, object?>()
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits toolExecutionEnd carrying the REAL result from FunctionResultContent.
    /// FunctionResultContent.Exception (M.E.AI 10.5.1) is set when the invoked function threw;
    /// isError derives from that property. fr.Result is the raw object? the AIFunction returned.
    /// </summary>
    private async Task EmitToolExecutionEndAsync(
        string callId,
        string toolName,
        FunctionResultContent fr,
        List<object> toolResults,
        int? argsSizeBytes,
        CancellationToken cancellationToken)
    {
        // FunctionResultContent.Exception is non-null when the AIFunction threw.
        bool isError = fr.Exception is not null;
        object result = isError
            ? new { error = fr.Exception!.Message }
            : (fr.Result ?? new { });

        using Activity? toolActivity = DmonTelemetry.Source.StartActivity("tool.execute");
        if (toolActivity is not null)
        {
            toolActivity.SetTag("dmon.tool.name", toolName);
            if (argsSizeBytes.HasValue)
                toolActivity.SetTag("dmon.tool.args.size_bytes", argsSizeBytes.Value);
            toolActivity.SetTag("dmon.tool.result.size_bytes",
                JsonSerializer.SerializeToUtf8Bytes(result).Length);
            toolActivity.SetTag("dmon.tool.is_error", isError);
        }

        await _emitter.EmitAsync(new ToolExecutionEndEvent
        {
            CallId = callId,
            Result = result,
            IsError = isError
        }, cancellationToken).ConfigureAwait(false);

        toolResults.Add(result);
        DmonTelemetry.RecordToolInvocation(toolName, isError);
    }

    /// <summary>
    /// For any callId that got a toolExecutionStart but never an observed FunctionResultContent,
    /// emit toolExecutionEnd with an error marker so the host UI never hangs.
    /// Always called with CancellationToken.None so these emits reach the host even on abort.
    /// </summary>
    internal async Task EmitMissingToolEndEventsAsync(
        HashSet<string> startedCallIds,
        HashSet<string> endedCallIds,
        Dictionary<string, FunctionCallContent> accumulatedCalls,
        List<object> toolResults,
        CancellationToken cancellationToken)
    {
        foreach (string callId in startedCallIds)
        {
            if (endedCallIds.Add(callId))
            {
                string toolName = accumulatedCalls.TryGetValue(callId, out FunctionCallContent? call)
                    ? call.Name
                    : callId;
                object errorResult = new { error = "Tool result not received from provider." };

                using Activity? toolActivity = DmonTelemetry.Source.StartActivity("tool.execute");
                if (toolActivity is not null)
                {
                    toolActivity.SetTag("dmon.tool.name", toolName);
                    toolActivity.SetTag("dmon.tool.is_error", true);
                    toolActivity.SetTag("dmon.tool.result.size_bytes",
                        JsonSerializer.SerializeToUtf8Bytes(errorResult).Length);
                }

                await _emitter.EmitAsync(new ToolExecutionEndEvent
                {
                    CallId = callId,
                    Result = errorResult,
                    IsError = true
                }, cancellationToken).ConfigureAwait(false);

                toolResults.Add(errorResult);
                DmonTelemetry.RecordToolInvocation(toolName, true);
            }
        }
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
