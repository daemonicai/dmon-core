using System.Text.Json.Serialization;

namespace Dmon.Core.Auth;

/// <summary>
/// Schema for a credentials file at <c>~/.dmon/credentials/&lt;provider&gt;.json</c>.
/// Per ADR-005: readers MUST ignore unknown fields to allow forward-compatible additions
/// (e.g. OAuth tokens in the future).
/// </summary>
public sealed record CredentialRecord
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("type")]
    public required string CredentialType { get; init; }

    [JsonPropertyName("apiKey")]
    public required string ApiKey { get; init; }

    [JsonPropertyName("headerStyle")]
    public string HeaderStyle { get; init; } = "bearer";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}
