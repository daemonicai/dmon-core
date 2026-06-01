using System.Text.Json.Serialization;
using Dmon.Protocol.Sessions;

namespace Dmon.Protocol.Events;

public sealed record SessionCreatedResultEvent : ResultEvent
{
    [JsonPropertyName("session")]
    public required SessionMeta Session { get; init; }
}

public sealed record SessionForkedResultEvent : ResultEvent
{
    [JsonPropertyName("session")]
    public required SessionMeta Session { get; init; }
}

public sealed record SessionClonedResultEvent : ResultEvent
{
    [JsonPropertyName("session")]
    public required SessionMeta Session { get; init; }
}

public sealed record SessionLoadedResultEvent : ResultEvent
{
    [JsonPropertyName("session")]
    public required SessionMeta Session { get; init; }
}

public sealed record SessionListResultEvent : ResultEvent
{
    [JsonPropertyName("sessions")]
    public required IReadOnlyList<SessionMeta> Sessions { get; init; }
}

public sealed record SessionStatsResultEvent : ResultEvent
{
    [JsonPropertyName("stats")]
    public required SessionStats Stats { get; init; }
}
