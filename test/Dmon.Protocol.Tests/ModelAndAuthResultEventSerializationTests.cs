using System.Text.Json;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Wire-shape assertions for model and auth result events (group 3 of the
/// typed-command-result-events change). Auth events have no emit sites today
/// (NullAuthHandler is a stub), so only type/serialization is verified here —
/// not an end-to-end emit path.
/// </summary>
public sealed class ModelAndAuthResultEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Serialize(Event evt) =>
        JsonSerializer.Serialize(evt, JsonOptions);

    private static Event Deserialize(string json) =>
        JsonSerializer.Deserialize<Event>(json, JsonOptions)
        ?? throw new InvalidOperationException("Deserialized null.");

    // ── model.listResult ─────────────────────────────────────────────────────

    [Fact]
    public void ModelListResultEvent_SerializesWithDiscriminatorAndId()
    {
        ModelListResultEvent evt = new()
        {
            CommandId = "cmd-list-1",
            Models = [new Model { Id = "claude-3", Name = "Claude 3", Provider = "anthropic", Input = [InputType.Text] }],
            ActiveProvider = "anthropic",
            ActiveModelId = "claude-3",
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("model.listResult", root.GetProperty("type").GetString());
        Assert.Equal("cmd-list-1", root.GetProperty("id").GetString());
    }

    [Fact]
    public void ModelListResultEvent_RoundTrips_PreservesCommandId()
    {
        ModelListResultEvent original = new()
        {
            CommandId = "cmd-list-rt",
            Models = [],
            ActiveProvider = "anthropic",
            ActiveModelId = string.Empty,
        };

        Event deserialized = Deserialize(Serialize(original));

        ModelListResultEvent result = Assert.IsType<ModelListResultEvent>(deserialized);
        Assert.Equal("cmd-list-rt", result.CommandId);
        Assert.Equal("anthropic", result.ActiveProvider);
    }

    // ── model.models.result ──────────────────────────────────────────────────

    [Fact]
    public void ModelModelsResultEvent_SerializesWithDiscriminatorAndId()
    {
        ModelModelsResultEvent evt = new()
        {
            CommandId = "cmd-models-1",
            Provider = "openai",
            Models = ["gpt-4o", "gpt-4o-mini"],
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("model.models.result", root.GetProperty("type").GetString());
        Assert.Equal("cmd-models-1", root.GetProperty("id").GetString());
        Assert.Equal("openai", root.GetProperty("provider").GetString());
    }

    [Fact]
    public void ModelModelsResultEvent_RoundTrips_PreservesCommandId()
    {
        ModelModelsResultEvent original = new()
        {
            CommandId = "cmd-models-rt",
            Provider = "openai",
            Models = ["gpt-4o"],
        };

        Event deserialized = Deserialize(Serialize(original));

        ModelModelsResultEvent result = Assert.IsType<ModelModelsResultEvent>(deserialized);
        Assert.Equal("cmd-models-rt", result.CommandId);
        Assert.Equal("openai", result.Provider);
    }

    // ── auth result events (contract-only — no emit sites exist today) ────────

    [Fact]
    public void AuthStatusResultEvent_SerializesWithDiscriminatorAndId()
    {
        AuthStatusResultEvent evt = new()
        {
            CommandId = "cmd-auth-status",
            Providers = [],
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("auth.statusResult", root.GetProperty("type").GetString());
        Assert.Equal("cmd-auth-status", root.GetProperty("id").GetString());
    }

    [Fact]
    public void AuthLoginCompleteEvent_SerializesWithDiscriminatorAndId()
    {
        AuthLoginCompleteEvent evt = new()
        {
            CommandId = "cmd-auth-login",
            Provider = "anthropic",
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("auth.loginComplete", root.GetProperty("type").GetString());
        Assert.Equal("cmd-auth-login", root.GetProperty("id").GetString());
        Assert.Equal("anthropic", root.GetProperty("provider").GetString());
    }

    [Fact]
    public void AuthLogoutCompleteEvent_SerializesWithDiscriminatorAndId()
    {
        AuthLogoutCompleteEvent evt = new()
        {
            CommandId = "cmd-auth-logout",
            Provider = "openai",
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("auth.logoutComplete", root.GetProperty("type").GetString());
        Assert.Equal("cmd-auth-logout", root.GetProperty("id").GetString());
    }

    [Fact]
    public void AuthLoginFailedEvent_SerializesWithDiscriminatorAndId()
    {
        AuthLoginFailedEvent evt = new()
        {
            CommandId = "cmd-auth-fail",
            Provider = "anthropic",
            Reason = "invalid key",
        };

        string json = Serialize(evt);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("auth.loginFailed", root.GetProperty("type").GetString());
        Assert.Equal("cmd-auth-fail", root.GetProperty("id").GetString());
        Assert.Equal("invalid key", root.GetProperty("reason").GetString());
    }

    [Fact]
    public void AuthResultEvents_AreResultEvents()
    {
        // Confirm the type hierarchy — auth events derive from ResultEvent,
        // not bare Event.
        Assert.True(typeof(ResultEvent).IsAssignableFrom(typeof(AuthStatusResultEvent)));
        Assert.True(typeof(ResultEvent).IsAssignableFrom(typeof(AuthLoginCompleteEvent)));
        Assert.True(typeof(ResultEvent).IsAssignableFrom(typeof(AuthLogoutCompleteEvent)));
        Assert.True(typeof(ResultEvent).IsAssignableFrom(typeof(AuthLoginFailedEvent)));
    }
}
