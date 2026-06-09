using System.Text.Json.Nodes;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Verifies the guarantees of <see cref="ProtocolSchemaExporter"/>:
///  - gw control-frame discriminators are emitted as {"const":"<value>"} and are required
///  - gw.ping and gw.pong are distinguishable (have different const values)
///  - omitted-nullable properties are absent from "required"
///  - the export is deterministic across two calls
///  - no third-party type names appear in the output
/// </summary>
public sealed class ProtocolSchemaExporterTests
{
    private static JsonObject GetDefs()
    {
        JsonNode schema = ProtocolSchemaExporter.Export();
        return (JsonObject)schema["$defs"]!;
    }

    // ── gw discriminator is a const, not a nullable string ───────────────────

    [Theory]
    [InlineData("gw.attach",         "attach")]
    [InlineData("gw.attached",       "attached")]
    [InlineData("gw.ack",            "ack")]
    [InlineData("gw.create",         "create")]
    [InlineData("gw.created",        "created")]
    [InlineData("gw.createRejected", "createRejected")]
    [InlineData("gw.ping",           "ping")]
    [InlineData("gw.pong",           "pong")]
    public void ControlFrame_GwProperty_IsConst(string defKey, string expectedConst)
    {
        JsonObject defs = GetDefs();
        JsonObject frame = (JsonObject)defs[defKey]!;
        JsonObject props = (JsonObject)frame["properties"]!;
        JsonObject gwProp = (JsonObject)props["gw"]!;

        // Must be {"const": "<value>"} — not a type schema.
        JsonValue constNode = (JsonValue)gwProp["const"]!;
        Assert.Equal(expectedConst, constNode.GetValue<string>());

        // Must not have a "type" key (which would indicate a nullable string schema).
        Assert.Null(gwProp["type"]);
    }

    [Theory]
    [InlineData("gw.attach")]
    [InlineData("gw.attached")]
    [InlineData("gw.ack")]
    [InlineData("gw.create")]
    [InlineData("gw.created")]
    [InlineData("gw.createRejected")]
    [InlineData("gw.ping")]
    [InlineData("gw.pong")]
    public void ControlFrame_Gw_IsRequired(string defKey)
    {
        JsonObject defs = GetDefs();
        JsonObject frame = (JsonObject)defs[defKey]!;
        JsonArray required = (JsonArray)frame["required"]!;

        bool gwIsRequired = required.Any(n =>
            n is JsonValue v && v.TryGetValue(out string? s) && s == "gw");

        Assert.True(gwIsRequired, $"{defKey}: 'gw' must be in the required array");
    }

    [Fact]
    public void GwPing_And_GwPong_AreDistinguishable()
    {
        JsonObject defs = GetDefs();
        JsonObject ping = (JsonObject)defs["gw.ping"]!;
        JsonObject pong = (JsonObject)defs["gw.pong"]!;

        string pingConst = ((JsonValue)((JsonObject)ping["properties"]!["gw"]!)["const"]!).GetValue<string>();
        string pongConst = ((JsonValue)((JsonObject)pong["properties"]!["gw"]!)["const"]!).GetValue<string>();

        Assert.NotEqual(pingConst, pongConst);
        Assert.Equal("ping", pingConst);
        Assert.Equal("pong", pongConst);
    }

    // ── omitted-nullable properties are not in required ──────────────────────

    [Fact]
    public void GwCreate_NullableProfile_IsNotRequired()
    {
        JsonObject defs = GetDefs();
        JsonObject frame = (JsonObject)defs["gw.create"]!;
        JsonArray required = (JsonArray)frame["required"]!;

        bool profileRequired = required.Any(n =>
            n is JsonValue v && v.TryGetValue(out string? s) && s == "profile");

        Assert.False(profileRequired, "gw.create: nullable 'profile' must not be in required (WhenWritingNull omits it)");
    }

    [Fact]
    public void SessionCreateCommand_NullableProfile_IsNotRequired()
    {
        JsonObject defs = GetDefs();
        JsonObject command = (JsonObject)defs["command"]!;
        JsonArray anyOf = (JsonArray)command["anyOf"]!;

        JsonObject? createCmd = anyOf
            .OfType<JsonObject>()
            .FirstOrDefault(o =>
                o["properties"] is JsonObject p &&
                p["type"] is JsonObject t &&
                t["const"] is JsonValue c &&
                c.TryGetValue(out string? v) && v == "session.create");

        Assert.NotNull(createCmd);

        JsonArray? required = createCmd["required"] as JsonArray;
        bool profileRequired = required?.Any(n =>
            n is JsonValue v && v.TryGetValue(out string? s) && s == "profile") ?? false;

        Assert.False(profileRequired, "session.create: nullable 'profile' must not be in required");
    }

    // ── determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void Export_IsDeterministic_AcrossTwoCalls()
    {
        string first  = ProtocolSchemaExporter.ExportAsJson();
        string second = ProtocolSchemaExporter.ExportAsJson();
        Assert.Equal(first, second);
    }

    // ── no third-party type names ─────────────────────────────────────────────

    [Fact]
    public void Export_ContainsNoThirdPartyTypeNames()
    {
        string json = ProtocolSchemaExporter.ExportAsJson();

        Assert.DoesNotContain("Microsoft",           json, StringComparison.Ordinal);
        Assert.DoesNotContain("Extensions",          json, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatMessage",         json, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent",           json, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerOptions", json, StringComparison.Ordinal);
    }
}
