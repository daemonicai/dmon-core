using System.Text.Json;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Dmon.Protocol.Sessions;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Wire-shape assertions for the session result event family (groups 2/4 of
/// the typed-command-result-events change). Each test round-trips through
/// the polymorphic <see cref="Event"/> serializer to confirm discriminator,
/// property names, and required fields survive the round trip.
/// </summary>
public sealed class SessionResultEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Serialize(Event evt) =>
        JsonSerializer.Serialize(evt, JsonOptions);

    private static Event Deserialize(string json) =>
        JsonSerializer.Deserialize<Event>(json, JsonOptions)
        ?? throw new InvalidOperationException("Deserialized null.");

    // ── session.createResult ─────────────────────────────────────────────────

    [Fact]
    public void SessionCreatedResultEvent_SerializesWithCorrectDiscriminatorAndFields()
    {
        SessionMeta meta = MakeMeta("s1");
        SessionCreatedResultEvent evt = new() { CommandId = "cmd-1", Session = meta };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("session.createResult", root.GetProperty("type").GetString());
        Assert.Equal("cmd-1",               root.GetProperty("id").GetString());
        Assert.Equal("s1",                  root.GetProperty("session").GetProperty("id").GetString());
    }

    [Fact]
    public void SessionCreatedResultEvent_RoundTrips()
    {
        SessionMeta meta = MakeMeta("s1");
        SessionCreatedResultEvent original = new() { CommandId = "cmd-1", Session = meta };

        Event deserialized = Deserialize(Serialize(original));

        SessionCreatedResultEvent result = Assert.IsType<SessionCreatedResultEvent>(deserialized);
        Assert.Equal("cmd-1", result.CommandId);
        Assert.Equal("s1",    result.Session.Id);
    }

    // ── session.forkResult ───────────────────────────────────────────────────

    [Fact]
    public void SessionForkedResultEvent_SerializesWithCorrectDiscriminator()
    {
        SessionForkedResultEvent evt = new() { CommandId = "cmd-2", Session = MakeMeta("s2") };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("session.forkResult", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("cmd-2",              doc.RootElement.GetProperty("id").GetString());
    }

    // ── session.cloneResult ──────────────────────────────────────────────────

    [Fact]
    public void SessionClonedResultEvent_SerializesWithCorrectDiscriminator()
    {
        SessionClonedResultEvent evt = new() { CommandId = "cmd-3", Session = MakeMeta("s3") };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("session.cloneResult", doc.RootElement.GetProperty("type").GetString());
    }

    // ── session.loadResult ───────────────────────────────────────────────────

    [Fact]
    public void SessionLoadedResultEvent_SerializesWithCorrectDiscriminator()
    {
        SessionLoadedResultEvent evt = new() { CommandId = "cmd-4", Session = MakeMeta("s4") };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("session.loadResult", doc.RootElement.GetProperty("type").GetString());
    }

    // ── session.listResult ───────────────────────────────────────────────────

    [Fact]
    public void SessionListResultEvent_SerializesAsTypedObjectWithSessions()
    {
        SessionMeta[] sessions = [MakeMeta("s1"), MakeMeta("s2")];
        SessionListResultEvent evt = new()
        {
            CommandId = "cmd-5",
            Sessions  = sessions
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("session.listResult", root.GetProperty("type").GetString());
        Assert.Equal("cmd-5",              root.GetProperty("id").GetString());

        JsonElement sessionsEl = root.GetProperty("sessions");
        Assert.Equal(JsonValueKind.Array, sessionsEl.ValueKind);
        Assert.Equal(2, sessionsEl.GetArrayLength());
        Assert.Equal("s1", sessionsEl[0].GetProperty("id").GetString());
        Assert.Equal("s2", sessionsEl[1].GetProperty("id").GetString());
    }

    [Fact]
    public void SessionListResultEvent_RoundTrips()
    {
        SessionListResultEvent original = new()
        {
            CommandId = "cmd-5",
            Sessions  = [MakeMeta("s1"), MakeMeta("s2")]
        };

        Event deserialized = Deserialize(Serialize(original));

        SessionListResultEvent result = Assert.IsType<SessionListResultEvent>(deserialized);
        Assert.Equal("cmd-5", result.CommandId);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("s1", result.Sessions[0].Id);
    }

    // ── session.getStatsResult ───────────────────────────────────────────────

    [Fact]
    public void SessionStatsResultEvent_SerializesTypedStatsObject()
    {
        SessionStatsResultEvent evt = new()
        {
            CommandId = "cmd-6",
            Stats     = new SessionStats
            {
                Tokens       = 1234,
                Cost         = 0.05m,
                ContextUsage = 42,
                CurrentModel = "claude-3-7-sonnet"
            }
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("session.getStatsResult", root.GetProperty("type").GetString());
        Assert.Equal("cmd-6",                  root.GetProperty("id").GetString());

        JsonElement stats = root.GetProperty("stats");
        Assert.Equal(1234,               stats.GetProperty("tokens").GetInt64());
        Assert.Equal(42,                 stats.GetProperty("contextUsage").GetInt32());
        Assert.Equal("claude-3-7-sonnet", stats.GetProperty("currentModel").GetString());
    }

    [Fact]
    public void SessionStatsResultEvent_RoundTrips()
    {
        SessionStatsResultEvent original = new()
        {
            CommandId = "cmd-6",
            Stats     = new SessionStats { Tokens = 99, Cost = 0.01m, ContextUsage = 10, CurrentModel = "gpt-4o" }
        };

        Event deserialized = Deserialize(Serialize(original));

        SessionStatsResultEvent result = Assert.IsType<SessionStatsResultEvent>(deserialized);
        Assert.Equal(99,      result.Stats.Tokens);
        Assert.Equal("gpt-4o", result.Stats.CurrentModel);
    }

    // ── commandError ─────────────────────────────────────────────────────────

    [Fact]
    public void CommandErrorEvent_SerializesWithIdCommandCodeMessage()
    {
        CommandErrorEvent evt = new()
        {
            CommandId = "cmd-7",
            Command   = "session.fork",
            Code      = "noActiveSession",
            Message   = "No active session to fork."
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("commandError",         root.GetProperty("type").GetString());
        Assert.Equal("cmd-7",                root.GetProperty("id").GetString());
        Assert.Equal("session.fork",         root.GetProperty("command").GetString());
        Assert.Equal("noActiveSession",      root.GetProperty("code").GetString());
        Assert.Equal("No active session to fork.", root.GetProperty("message").GetString());
    }

    [Fact]
    public void CommandErrorEvent_RoundTrips()
    {
        CommandErrorEvent original = new()
        {
            CommandId = "cmd-7",
            Command   = "session.clone",
            Code      = "noActiveSession",
            Message   = "No active session to clone."
        };

        Event deserialized = Deserialize(Serialize(original));

        CommandErrorEvent result = Assert.IsType<CommandErrorEvent>(deserialized);
        Assert.Equal("cmd-7",          result.CommandId);
        Assert.Equal("session.clone",  result.Command);
        Assert.Equal("noActiveSession", result.Code);
    }

    // ── session.getMessagesResult ─────────────────────────────────────────────

    [Fact]
    public void SessionMessagesResultEvent_SerializesWithCorrectDiscriminatorAndFields()
    {
        SessionMessagesResultEvent evt = new()
        {
            CommandId = "req-2",
            Messages  =
            [
                new MessageRecord
                {
                    EntryId   = "e1",
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Role      = "user",
                    Parts     = [new TextPart { Text = "hello" }]
                }
            ]
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("session.getMessagesResult", root.GetProperty("type").GetString());
        Assert.Equal("req-2",                     root.GetProperty("id").GetString());
        Assert.Equal(1,                           root.GetProperty("messages").GetArrayLength());
        Assert.Equal("message",                   root.GetProperty("messages")[0].GetProperty("type").GetString());
        Assert.Equal("user",                      root.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public void SessionMessagesResultEvent_SerializedJson_ContainsNoThirdPartyTypes()
    {
        SessionMessagesResultEvent evt = new()
        {
            CommandId = "req-2",
            Messages  =
            [
                new MessageRecord
                {
                    EntryId   = "e1",
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Role      = "assistant",
                    Parts     = [new TextPart { Text = "hi" }]
                }
            ]
        };

        string json = Serialize(evt);

        Assert.DoesNotContain("ChatMessage",  json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AIContent",    json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft",    json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionMessagesResultEvent_RoundTrips()
    {
        SessionMessagesResultEvent original = new()
        {
            CommandId = "req-2",
            Messages  =
            [
                new MessageRecord
                {
                    EntryId   = "e1",
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Role      = "user",
                    Parts     = [new TextPart { Text = "hello" }]
                },
                new CompactionMessage
                {
                    EntryId        = "c1",
                    Timestamp      = DateTimeOffset.UnixEpoch,
                    Summary        = "compacted",
                    SupersedesUpTo = "e0",
                    Reason         = "threshold",
                    TokensBefore   = 100
                }
            ]
        };

        Event deserialized = Deserialize(Serialize(original));

        SessionMessagesResultEvent result = Assert.IsType<SessionMessagesResultEvent>(deserialized);
        Assert.Equal("req-2",  result.CommandId);
        Assert.Equal(2,        result.Messages.Count);
        MessageRecord msg = Assert.IsType<MessageRecord>(result.Messages[0]);
        Assert.Equal("user",   msg.Role);
        Assert.IsType<CompactionMessage>(result.Messages[1]);
    }

    [Fact]
    public void SessionMessagesResultEvent_EmptyMessages_RoundTrips()
    {
        SessionMessagesResultEvent original = new() { CommandId = "req-2", Messages = [] };

        Event deserialized = Deserialize(Serialize(original));

        SessionMessagesResultEvent result = Assert.IsType<SessionMessagesResultEvent>(deserialized);
        Assert.Equal("req-2", result.CommandId);
        Assert.Empty(result.Messages);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SessionMeta MakeMeta(string id) => new()
    {
        Id       = id,
        Name     = null,
        Created  = DateTimeOffset.UtcNow,
        Modified = DateTimeOffset.UtcNow
    };
}
