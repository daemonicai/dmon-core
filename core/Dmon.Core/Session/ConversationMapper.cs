using System.Text.Json;
using Dmon.Protocol.Conversation;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Session;

/// <summary>
/// Maps between <see cref="ChatMessage"/> (M.E.AI) and the dmon-owned <see cref="Part"/> list.
///
/// This is the sole component that bridges M.E.AI types and the dmon-owned record format.
/// It must never appear in Dmon.Protocol — Protocol must remain free of third-party types.
///
/// Write side: lossless — every AIContent maps to a Part; unrecognised content becomes UnknownPart.
/// Read side: lossy replay subset — only text/toolCall/toolResult/image are reconstructed;
///   reasoning/usage/unknown are preserved in the record but excluded from replay context.
/// </summary>
public static class ConversationMapper
{
    /// <summary>
    /// Maps the contents of <paramref name="message"/> to a list of <see cref="Part"/> values.
    /// Never throws; unrecognised content is preserved as <see cref="UnknownPart"/>.
    /// </summary>
    /// <param name="message">The message to map.</param>
    /// <param name="producedBy">
    /// Source stamp written into <see cref="UnknownPart.ProducedBy"/> for any unrecognised content.
    /// Callers should pass an identifier such as the provider adapter name.
    /// </param>
    /// <returns>
    /// The role string (e.g. "assistant") and the mapped parts.
    /// entryId and timestamp are NOT set here — they are minted by session-storage at append time.
    /// </returns>
    public static (string Role, IReadOnlyList<Part> Parts) ToParts(
        ChatMessage message,
        string producedBy = "unknown")
    {
        string role = message.Role.Value;
        List<Part> parts = new(message.Contents.Count);

        foreach (AIContent content in message.Contents)
        {
            Part part = content switch
            {
                TextContent tc => new TextPart { Text = tc.Text },
                FunctionCallContent fc => MapFunctionCall(fc),
                FunctionResultContent fr => MapFunctionResult(fr),
                _ => MapUnknown(content, producedBy)
            };
            parts.Add(part);
        }

        return (role, parts);
    }

    /// <summary>
    /// Reconstructs a <see cref="ChatMessage"/> from a <see cref="MessageRecord"/> for replay.
    /// Only text/toolCall/toolResult/image parts are included; reasoning/usage/unknown are skipped.
    /// </summary>
    public static ChatMessage ToMessage(MessageRecord record)
    {
        ChatRole role = new(record.Role);
        List<AIContent> contents = [];

        foreach (Part part in record.Parts)
        {
            AIContent? content = part switch
            {
                TextPart tp => new TextContent(tp.Text),
                ToolCallPart tc => MapToolCallToContent(tc),
                ToolResultPart tr => MapToolResultToContent(tr),
                ImagePart ip => MapImageToContent(ip),
                // ReasoningPart, UsagePart, UnknownPart — excluded from replay
                _ => null
            };

            if (content is not null)
                contents.Add(content);
        }

        return new ChatMessage(role, contents);
    }

    private static ToolCallPart MapFunctionCall(FunctionCallContent fc)
    {
        JsonElement args = fc.Arguments is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(fc.Arguments);

        return new ToolCallPart
        {
            CallId = fc.CallId,
            Name = fc.Name,
            Args = args
        };
    }

    private static ToolResultPart MapFunctionResult(FunctionResultContent fr)
    {
        JsonElement? result = fr.Result is null
            ? null
            : JsonSerializer.SerializeToElement(fr.Result);

        return new ToolResultPart
        {
            CallId = fr.CallId,
            Result = result,
            // attachmentRef stays null — offloading is group 3
            IsError = false,
            Truncated = false
        };
    }

    private static UnknownPart MapUnknown(AIContent content, string producedBy)
    {
        JsonElement raw;
        try
        {
            raw = JsonSerializer.SerializeToElement(content, content.GetType());
        }
        catch
        {
            // Last resort: use the type name as the raw payload so no information is silently dropped.
            raw = JsonSerializer.SerializeToElement(new { type = content.GetType().FullName });
        }

        return new UnknownPart
        {
            Raw = raw,
            ProducedBy = producedBy.Length > 0 ? producedBy : content.GetType().Name
        };
    }

    private static FunctionCallContent MapToolCallToContent(ToolCallPart tc)
    {
        Dictionary<string, object?> arguments = [];

        if (tc.Args.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in tc.Args.EnumerateObject())
                arguments[prop.Name] = prop.Value.Deserialize<object?>();
        }

        return new FunctionCallContent(tc.CallId, tc.Name, arguments);
    }

    private static FunctionResultContent? MapToolResultToContent(ToolResultPart tr)
    {
        if (tr.Result is null)
        {
            // Result was offloaded or is absent — omit gracefully rather than replaying null.
            return null;
        }

        object? result = tr.Result.Value.Deserialize<object?>();
        return new FunctionResultContent(tr.CallId, result);
    }

    private static DataContent? MapImageToContent(ImagePart ip)
    {
        if (ip.DataBase64 is null)
        {
            // Only attachmentRef available — file IO is out of scope for this group; skip.
            return null;
        }

        byte[] bytes = Convert.FromBase64String(ip.DataBase64);
        return new DataContent(bytes, ip.MediaType);
    }
}
