using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// Base record for all command-correlated result events.
/// Every result event carries the originating command <see cref="CommandId"/> so
/// the host can correlate responses without polling.
/// </summary>
public abstract record ResultEvent : Event
{
    [JsonPropertyName("id")]
    public required string CommandId { get; init; }
}
