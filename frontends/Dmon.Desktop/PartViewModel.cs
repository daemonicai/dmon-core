using System.Text.Json;
using Dmon.Protocol.Conversation;
using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// Base class for view-models that wrap a single <see cref="Part"/> for display.
/// Each sealed subtype corresponds to one Part discriminant.
/// </summary>
public abstract class PartViewModel : ReactiveObject
{
    /// <summary>Factory: maps a <see cref="Part"/> to its presentation view-model.</summary>
    public static PartViewModel From(Part part) => part switch
    {
        TextPart text         => new TextPartViewModel(text),
        ToolCallPart toolCall => new ToolCallPartViewModel(toolCall),
        ToolResultPart result => new ToolResultPartViewModel(result),
        ImagePart image       => new ImagePartViewModel(image),
        ReasoningPart reason  => new ReasoningPartViewModel(reason),
        UsagePart usage       => new UsagePartViewModel(usage),
        UnknownPart unknown   => new UnknownPartViewModel(unknown),
        _                     => throw new ArgumentOutOfRangeException(nameof(part),
                                     $"Unmodelled Part subtype '{part.GetType().FullName}' has no PartViewModel mapping. " +
                                     "Add an explicit arm or map to UnknownPartViewModel.")
    };
}

/// <summary>Presentation VM for a <see cref="TextPart"/>. Text is mutable to support streaming.</summary>
public sealed class TextPartViewModel : PartViewModel
{
    private string _text;

    public TextPartViewModel(TextPart part)
    {
        _text = part.Text;
    }

    public string Text
    {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }
}

public sealed class ToolCallPartViewModel : PartViewModel
{
    public ToolCallPartViewModel(ToolCallPart part)
    {
        CallId = part.CallId;
        Name = part.Name;
        Args = part.Args.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : part.Args.ToString();
    }

    public string CallId { get; }
    public string Name { get; }
    public string Args { get; }
}

public sealed class ToolResultPartViewModel : PartViewModel
{
    public ToolResultPartViewModel(ToolResultPart part)
    {
        CallId = part.CallId;
        IsError = part.IsError;
        Truncated = part.Truncated;
        Summary = part.Result?.ValueKind is JsonValueKind.Undefined or null
            ? (part.AttachmentRef ?? "(no result)")
            : part.Result.Value.ToString();
    }

    public string CallId { get; }
    public bool IsError { get; }
    public bool Truncated { get; }
    public string Summary { get; }
}

public sealed class ImagePartViewModel : PartViewModel
{
    public ImagePartViewModel(ImagePart part)
    {
        MediaType = part.MediaType;
        HasAttachment = part.AttachmentRef is not null;
        AttachmentRef = part.AttachmentRef;
        DataBase64 = part.DataBase64;
    }

    public string MediaType { get; }
    public bool HasAttachment { get; }
    public string? AttachmentRef { get; }
    public string? DataBase64 { get; }
}

public sealed class ReasoningPartViewModel : PartViewModel
{
    public ReasoningPartViewModel(ReasoningPart part)
    {
        Text = part.Text;
    }

    public string Text { get; }
}

/// <summary>
/// UsagePart is render-only metadata — shown as a faint token count summary.
/// Never replayed to the model.
/// </summary>
public sealed class UsagePartViewModel : PartViewModel
{
    public UsagePartViewModel(UsagePart part)
    {
        InputTokens = part.InputTokens;
        OutputTokens = part.OutputTokens;
    }

    public long InputTokens { get; }
    public long OutputTokens { get; }
    public string DisplayTokens => $"in:{InputTokens} out:{OutputTokens} tok";
}

/// <summary>
/// UnknownPart is render-only. Raw JSON is shown verbatim so no information is lost;
/// it MUST NOT appear in any outbound command payload.
/// </summary>
public sealed class UnknownPartViewModel : PartViewModel
{
    public UnknownPartViewModel(UnknownPart part)
    {
        RawJson = part.Raw.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : part.Raw.ToString();
        ProducedBy = part.ProducedBy;
    }

    public string RawJson { get; }
    public string ProducedBy { get; }

    /// <summary>
    /// Convenience display label combining producer and truncated JSON.
    /// </summary>
    public string DisplayLabel => $"[unknown:{ProducedBy}] {Truncate(RawJson, 80)}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
