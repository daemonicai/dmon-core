using System.Text.Json.Serialization;
using Daemon.Protocol.Models;
using Daemon.Protocol.Enums;

namespace Daemon.Protocol.Events;

public sealed record BootstrapNoticeEvent : Event
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("created")]
    public required IReadOnlyList<string> Created { get; init; }
}

public sealed record ProviderSwitchedEvent : Event
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("effectiveNextTurn")]
    public bool EffectiveNextTurn { get; init; }
}

public sealed record CapabilityIgnoredEvent : Event
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("requestedValue")]
    public required string RequestedValue { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record ExtensionErrorEvent : Event
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<object> Diagnostics { get; init; }
}

public sealed record RetryAttemptEvent : Event
{
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; init; }

    [JsonPropertyName("nextDelayMs")]
    public int NextDelayMs { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record ErrorEvent : Event
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("recoverable")]
    public bool Recoverable { get; init; }
}