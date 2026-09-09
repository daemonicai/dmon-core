using System.Text.Json;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Wire-shape assertions for <see cref="SessionStartedEvent"/> (lazy-session-creation change,
/// tasks 1.2/1.3). This event is a non-command notification (ADR-015 §2/§3): it must not derive
/// from <see cref="ResultEvent"/> and must carry no command-correlation <c>id</c>.
/// </summary>
public sealed class SessionStartedEventSerializationTests
{
    private static SessionMeta MakeMeta(string id) => new()
    {
        Id       = id,
        Name     = null,
        Created  = DateTimeOffset.UnixEpoch,
        Modified = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void SessionStartedEvent_IsNotAResultEvent()
    {
        SessionStartedEvent evt = new() { Session = MakeMeta("s1") };

        Assert.IsNotAssignableFrom<ResultEvent>(evt);
    }

    [Fact]
    public void SessionStartedEvent_SerializesWithDiscriminatorAndSessionPayload_NoId()
    {
        SessionStartedEvent evt = new() { Session = MakeMeta("s1") };

        string json = JsonSerializer.Serialize<Event>(evt, WireSerializerOptions.Default);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("sessionStarted", root.GetProperty("type").GetString());
        Assert.Equal("s1", root.GetProperty("session").GetProperty("id").GetString());

        // ADR-015: a non-command event carries no command-correlation "id" at all —
        // the property must be absent, not merely null.
        Assert.False(root.TryGetProperty("id", out _), "sessionStarted must not carry an 'id' key.");
    }

    [Fact]
    public void SessionStartedEvent_RoundTripsThroughEventBase()
    {
        SessionStartedEvent original = new() { Session = MakeMeta("s1") };

        string json = JsonSerializer.Serialize<Event>(original, WireSerializerOptions.Default);
        Event deserialized = JsonSerializer.Deserialize<Event>(json, WireSerializerOptions.Default)
            ?? throw new InvalidOperationException("Deserialized null.");

        SessionStartedEvent result = Assert.IsType<SessionStartedEvent>(deserialized);
        Assert.Equal("s1", result.Session.Id);
    }

    [Fact]
    public void ExportedSchema_DeclaresSessionStartedDiscriminator()
    {
        // Demonstrates the freshness gate is meaningful: this reads the LIVE export (not the
        // committed docs/protocol/schema.json), so deleting SessionStartedEvent's
        // [JsonDerivedType] registration reddens this test immediately, independent of
        // whether docs/protocol/schema.json has been regenerated.
        string json = ProtocolSchemaExporter.ExportAsJson();

        Assert.Contains("\"sessionStarted\"", json, StringComparison.Ordinal);
    }
}
