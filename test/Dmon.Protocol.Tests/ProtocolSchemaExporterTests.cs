using System.Text;
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

    // ── platform invariance: output must be pure LF ──────────────────────────

    [Fact]
    public void ExportAsJson_ContainsNoCr()
    {
        string json = ProtocolSchemaExporter.ExportAsJson();
        Assert.DoesNotContain('\r', json);
    }

    // ── freshness gate: committed artifact must match live types ─────────────

    [Fact]
    public void CommittedSchema_MatchesLiveExport()
    {
        // Walk up from the test binary to the repository root.
        // Test assembly sits at: <repo>/test/Dmon.Protocol.Tests/bin/<cfg>/<tfm>/
        // Five levels up lands at <repo>.
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        string committedPath = Path.Combine(repoRoot, "docs", "protocol", "schema.json");

        Assert.True(
            File.Exists(committedPath),
            $"Committed schema not found at '{committedPath}'. Has it been generated yet? Run: make schema");

        // Compare bytes so encoding or trailing-newline differences are caught.
        byte[] committedBytes = File.ReadAllBytes(committedPath);
        byte[] liveBytes      = Encoding.UTF8.GetBytes(ProtocolSchemaExporter.ExportAsJson());

        if (!committedBytes.SequenceEqual(liveBytes))
        {
            string committedText = Encoding.UTF8.GetString(committedBytes);
            string liveText      = Encoding.UTF8.GetString(liveBytes);

            int firstDiff = -1;
            int minLen = Math.Min(committedText.Length, liveText.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (committedText[i] != liveText[i])
                {
                    firstDiff = i;
                    break;
                }
            }

            string hint = firstDiff >= 0
                ? $"First difference at character {firstDiff}: " +
                  $"committed='{Escape(committedText, firstDiff)}' " +
                  $"live='{Escape(liveText, firstDiff)}'"
                : $"Length differs: committed={committedBytes.Length} bytes, live={liveBytes.Length} bytes";

            Assert.Fail(
                $"docs/protocol/schema.json is stale — it does not match the output of " +
                $"ProtocolSchemaExporter.ExportAsJson(). {hint}. " +
                $"Regenerate it by running: make schema");
        }
    }

    private static string Escape(string text, int index)
    {
        int start  = Math.Max(0, index - 10);
        int length = Math.Min(20, text.Length - start);
        return text.Substring(start, length)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
    }
}
