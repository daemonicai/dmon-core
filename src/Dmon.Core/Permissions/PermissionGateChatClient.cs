using System.Diagnostics;
using Dmon.Core.Extensions;
using Dmon.Core.Telemetry;
using Dmon.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Permissions;

/// <summary>
/// IChatClient middleware that intercepts tool calls and evaluates each against
/// IPermissionPolicy before FunctionInvokingChatClient dispatches them.
///
/// IChatClient pipeline (assembled in Group 9 startup):
///   1. PermissionGateChatClient   ← evaluate policy, prompt/deny
///   2. FunctionInvokingChatClient ← M.E.AI dispatch loop
///   3. actual provider client
/// </summary>
public sealed class PermissionGateChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IPermissionPolicy _policy;
    private readonly IToolRegistry _registry;
    private readonly Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> _confirmCallback;

    public PermissionGateChatClient(
        IChatClient inner,
        IPermissionPolicy policy,
        IToolRegistry registry,
        Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> confirmCallback)
    {
        _inner = inner;
        _policy = policy;
        _registry = registry;
        _confirmCallback = confirmCallback;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await _inner.GetResponseAsync(messages, options, cancellationToken);
        List<ChatMessage> gated = await ApplyGateAsync(response.Messages, cancellationToken);
        return new ChatResponse(gated)
        {
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            ResponseId = response.ResponseId,
            AdditionalProperties = response.AdditionalProperties
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Buffer all updates so complete tool calls can be evaluated before any are emitted.
        List<ChatResponseUpdate> buffered = [];

        await foreach (ChatResponseUpdate update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            buffered.Add(update);
        }

        List<ChatMessage> assembled = AssembleStreamingUpdates(buffered);
        List<ChatMessage> gated = await ApplyGateAsync(assembled, cancellationToken);

        foreach (ChatMessage message in gated)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    // --- Gate logic ---

    private async Task<List<ChatMessage>> ApplyGateAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> result = [];

        foreach (ChatMessage message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                result.Add(message);
                continue;
            }

            List<AIContent> allowedContents = [];
            List<ChatMessage> deniedResults = [];

            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionCallContent call)
                {
                    allowedContents.Add(content);
                    continue;
                }

                using Activity? permActivity = DmonTelemetry.Source.StartActivity("permission.evaluate");
                if (permActivity is not null)
                {
                    permActivity.SetTag("dmon.tool.name", call.Name);
                }

                IDmonExtension? extension = _registry.FindExtension(call.Name);
                PermissionResult permission = extension?.Evaluate(call, _policy.ProjectSettings, _policy.GlobalSettings)
                    ?? PermissionResult.Prompt;
                string decision = permission.ToString().ToLowerInvariant();
                ToolConfirmRequest confirmReq = extension?.CreateConfirmRequest(call)
                    ?? new ToolConfirmRequest
                    {
                        Id = call.CallId,
                        Name = call.Name,
                        Args = call.Arguments is null
                            ? new Dictionary<string, object?>()
                            : new Dictionary<string, object?>(call.Arguments),
                        Risk = RiskLevel.Low
                    };
                string riskLevel = confirmReq.Risk.ToString().ToLowerInvariant();

                switch (permission)
                {
                    case PermissionResult.Allow:
                        allowedContents.Add(call);
                        decision = "allow";
                        break;

                    case PermissionResult.Deny:
                        deniedResults.Add(BuildDeniedToolResult(call));
                        decision = "deny";
                        break;

                    case PermissionResult.Prompt:
                        bool confirmed = await PromptForToolCallAsync(confirmReq, cancellationToken);
                        if (confirmed)
                        {
                            allowedContents.Add(call);
                            decision = "allowonce";
                        }
                        else
                        {
                            deniedResults.Add(BuildDeniedToolResult(call));
                            decision = "deny";
                        }
                        break;
                }

                if (permActivity is not null)
                {
                    permActivity.SetTag("dmon.permission.risk", riskLevel);
                    permActivity.SetTag("dmon.permission.decision", decision);
                }

                DmonTelemetry.RecordPermissionPrompt(riskLevel, decision);
            }

            // Emit the assistant message (with allowed tool calls) before any denied results.
            if (allowedContents.Count > 0)
            {
                result.Add(new ChatMessage(message.Role, allowedContents));
            }

            result.AddRange(deniedResults);
        }

        return result;
    }

    private async Task<bool> PromptForToolCallAsync(
        ToolConfirmRequest req,
        CancellationToken cancellationToken)
    {
        ToolConfirmRequestEvent evt = new()
        {
            ConfirmId = req.Id,
            Name = req.Name,
            Args = req.Args,
            Risk = req.Risk
        };

        return await _confirmCallback(evt, cancellationToken);
    }

    private static ChatMessage BuildDeniedToolResult(FunctionCallContent call)
    {
        FunctionResultContent result = new(call.CallId, "Tool call denied by permission policy.");
        return new ChatMessage(ChatRole.Tool, [result]);
    }

    private static List<ChatMessage> AssembleStreamingUpdates(List<ChatResponseUpdate> updates)
    {
        List<ChatMessage> messages = [];
        List<AIContent> currentContents = [];
        ChatRole? currentRole = null;

        foreach (ChatResponseUpdate update in updates)
        {
            if (currentRole.HasValue && update.Role.HasValue && update.Role != currentRole)
            {
                messages.Add(new ChatMessage(currentRole.Value, currentContents));
                currentContents = [];
            }

            if (update.Role.HasValue)
            {
                currentRole = update.Role;
            }

            currentContents.AddRange(update.Contents);
        }

        if (currentRole.HasValue && currentContents.Count > 0)
        {
            messages.Add(new ChatMessage(currentRole.Value, currentContents));
        }

        return messages;
    }
}
