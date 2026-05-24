using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Events;

public sealed record ToolConfirmRequestEvent : Event
{
    [JsonPropertyName("id")]
    public required string ConfirmId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("args")]
    public required object Args { get; init; }

    [JsonPropertyName("risk")]
    public RiskLevel Risk { get; init; }
}

public sealed record SessionUpdatedEvent : Event
{
    [JsonPropertyName("id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

public sealed record ExtensionLoadedEvent : Event
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("tools")]
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>
    /// Non-null when the extension registered a provider.
    /// Null when the extension was inapplicable or had no provider.
    /// </summary>
    [JsonPropertyName("providerName")]
    public string? ProviderName { get; init; }
}

public sealed record ExtensionUnloadedEvent : Event
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record CompactionStartEvent : Event
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record CompactionEndEvent : Event
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("aborted")]
    public bool Aborted { get; init; }
}