using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Verifies the guarantees of <see cref="ProtocolSchemaExporter"/>:
///  - every [JsonDerivedType] leaf of Command, Event, and Part appears in the schema
///  - gw control-frame discriminators are emitted as {"const":"<value>"} and are required
///  - all 8 gw frames are present as a family distinct from the type-keyed families
///  - gw.ping and gw.pong are distinguishable (have different const values)
///  - omitted-nullable properties are absent from "required"
///  - session.getMessagesResult references all 7 Part types in its messages payload
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

    // ── 4.1: every [JsonDerivedType] leaf is present in the schema ───────────
    //
    // The expected discriminator set is derived from the live [JsonDerivedType] attributes
    // via reflection, so a new leaf added to Command, Event, or Part without a corresponding
    // schema entry will make this test fail immediately.

    [Fact]
    public void AllCommandLeaves_AppearInSchema_KeyedByTypeDiscriminator()
    {
        IReadOnlySet<string> expectedDiscriminators = GetJsonDerivedTypeDiscriminators(typeof(Command));
        Assert.NotEmpty(expectedDiscriminators); // guard: reflection must produce results

        JsonObject defs = GetDefs();
        JsonObject command = (JsonObject)defs["command"]!;
        JsonArray anyOf = (JsonArray)command["anyOf"]!;

        HashSet<string> schemaDiscriminators = ExtractTypeConstValues(anyOf);

        IEnumerable<string> missing = expectedDiscriminators.Except(schemaDiscriminators);
        Assert.Empty(missing);

        // The schema must not have MORE command leaves than the source type declares.
        IEnumerable<string> extra = schemaDiscriminators.Except(expectedDiscriminators);
        Assert.Empty(extra);
    }

    [Fact]
    public void AllEventLeaves_AppearInSchema_KeyedByTypeDiscriminator()
    {
        IReadOnlySet<string> expectedDiscriminators = GetJsonDerivedTypeDiscriminators(typeof(Event));
        Assert.NotEmpty(expectedDiscriminators);

        JsonObject defs = GetDefs();
        JsonObject eventDef = (JsonObject)defs["event"]!;
        JsonArray anyOf = (JsonArray)eventDef["anyOf"]!;

        HashSet<string> schemaDiscriminators = ExtractTypeConstValues(anyOf);

        IEnumerable<string> missing = expectedDiscriminators.Except(schemaDiscriminators);
        Assert.Empty(missing);

        IEnumerable<string> extra = schemaDiscriminators.Except(expectedDiscriminators);
        Assert.Empty(extra);
    }

    [Fact]
    public void AllPartLeaves_AppearInSchema_KeyedByTypeDiscriminator()
    {
        IReadOnlySet<string> expectedDiscriminators = GetJsonDerivedTypeDiscriminators(typeof(Part));
        Assert.NotEmpty(expectedDiscriminators);

        JsonObject defs = GetDefs();
        JsonObject partDef = (JsonObject)defs["part"]!;
        JsonArray anyOf = (JsonArray)partDef["anyOf"]!;

        HashSet<string> schemaDiscriminators = ExtractTypeConstValues(anyOf);

        IEnumerable<string> missing = expectedDiscriminators.Except(schemaDiscriminators);
        Assert.Empty(missing);

        IEnumerable<string> extra = schemaDiscriminators.Except(expectedDiscriminators);
        Assert.Empty(extra);
    }

    [Fact]
    public void CommandLeaf_PropertyNames_AreCamelCase()
    {
        JsonObject defs = GetDefs();
        JsonObject command = (JsonObject)defs["command"]!;
        JsonArray anyOf = (JsonArray)command["anyOf"]!;

        // Check every leaf object's property keys.
        foreach (JsonObject leaf in anyOf.OfType<JsonObject>())
        {
            if (leaf["properties"] is not JsonObject props)
                continue;

            foreach (string propertyKey in props.Select(kv => kv.Key))
            {
                // Skip the type discriminator itself ("type" is all-lower, fine).
                // Property keys must start with a lowercase letter.
                Assert.True(
                    char.IsLower(propertyKey[0]) || propertyKey[0] == '_' || propertyKey[0] == '$',
                    $"Command leaf has PascalCase property key '{propertyKey}'. Expected camelCase.");
            }
        }
    }

    [Fact]
    public void EventLeaf_PropertyNames_AreCamelCase()
    {
        JsonObject defs = GetDefs();
        JsonObject eventDef = (JsonObject)defs["event"]!;
        JsonArray anyOf = (JsonArray)eventDef["anyOf"]!;

        foreach (JsonObject leaf in anyOf.OfType<JsonObject>())
        {
            if (leaf["properties"] is not JsonObject props)
                continue;

            foreach (string propertyKey in props.Select(kv => kv.Key))
            {
                Assert.True(
                    char.IsLower(propertyKey[0]) || propertyKey[0] == '_' || propertyKey[0] == '$',
                    $"Event leaf has PascalCase property key '{propertyKey}'. Expected camelCase.");
            }
        }
    }

    [Fact]
    public void PartLeaf_PropertyNames_AreCamelCase()
    {
        JsonObject defs = GetDefs();
        JsonObject partDef = (JsonObject)defs["part"]!;
        JsonArray anyOf = (JsonArray)partDef["anyOf"]!;

        foreach (JsonObject leaf in anyOf.OfType<JsonObject>())
        {
            if (leaf["properties"] is not JsonObject props)
                continue;

            foreach (string propertyKey in props.Select(kv => kv.Key))
            {
                Assert.True(
                    char.IsLower(propertyKey[0]) || propertyKey[0] == '_' || propertyKey[0] == '$',
                    $"Part leaf has PascalCase property key '{propertyKey}'. Expected camelCase.");
            }
        }
    }

    // ── 4.2: all 8 gw frames present as a distinct family ────────────────────
    //
    // The per-frame gw-const and gw-required assertions above (ControlFrame_GwProperty_IsConst
    // and ControlFrame_Gw_IsRequired) verify individual frames. This test verifies the
    // FAMILY-level invariants: exactly 8 frames are present, each is keyed by a "gw" property
    // (not "type"), and none bleeds into the command or event anyOf families.

    [Fact]
    public void ControlFrameFamily_AllEightFramesPresent_InDefs()
    {
        string[] expectedGwKeys =
        [
            "gw.attach", "gw.attached", "gw.ack",
            "gw.create", "gw.created", "gw.createRejected",
            "gw.ping", "gw.pong",
        ];

        JsonObject defs = GetDefs();

        foreach (string key in expectedGwKeys)
        {
            Assert.True(defs.ContainsKey(key), $"$defs is missing control frame '{key}'");
        }

        // Exactly 8 gw.* keys — no extras.
        int actualGwCount = defs.Count(kv => kv.Key.StartsWith("gw.", StringComparison.Ordinal));
        Assert.Equal(8, actualGwCount);
    }

    [Fact]
    public void ControlFrameFamily_DiscriminatorIsGw_NotType()
    {
        string[] gwKeys = ["gw.attach", "gw.attached", "gw.ack", "gw.create", "gw.created", "gw.createRejected", "gw.ping", "gw.pong"];
        JsonObject defs = GetDefs();

        foreach (string key in gwKeys)
        {
            JsonObject frame = (JsonObject)defs[key]!;
            JsonObject props = (JsonObject)frame["properties"]!;

            // Has "gw" with a const value.
            Assert.NotNull(props["gw"]);
            JsonObject gwProp = (JsonObject)props["gw"]!;
            Assert.NotNull(gwProp["const"]);

            // Must NOT have a "type" property that acts as the discriminator
            // (gw frames are not part of the ADR-003 "type" channel).
            bool hasTypeConst =
                props["type"] is JsonObject typeProp &&
                typeProp["const"] is not null;

            Assert.False(hasTypeConst, $"{key}: frame must not carry a 'type' const discriminator; frames use 'gw'.");
        }
    }

    [Fact]
    public void ControlFrameFamily_NoneBleedIntoCommandOrEventAnyOf()
    {
        // Collect all gw discriminator values.
        string[] gwValues = ["attach", "attached", "ack", "create", "created", "createRejected", "ping", "pong"];
        HashSet<string> gwSet = new(gwValues, StringComparer.Ordinal);

        JsonObject defs = GetDefs();

        // Command anyOf: no entry should have type.const equal to a gw value.
        JsonArray commandAnyOf = (JsonArray)((JsonObject)defs["command"]!)["anyOf"]!;
        foreach (string disc in ExtractTypeConstValues(commandAnyOf))
        {
            Assert.DoesNotContain(disc, gwSet);
        }

        // Event anyOf: same check.
        JsonArray eventAnyOf = (JsonArray)((JsonObject)defs["event"]!)["anyOf"]!;
        foreach (string disc in ExtractTypeConstValues(eventAnyOf))
        {
            Assert.DoesNotContain(disc, gwSet);
        }
    }

    // ── 4.3: session.getMessagesResult references Part union; no third-party type names ─

    [Fact]
    public void SessionGetMessagesResult_MessagesPayload_ContainsAllPartTypes()
    {
        IReadOnlySet<string> expectedPartDiscriminators = GetJsonDerivedTypeDiscriminators(typeof(Part));
        Assert.NotEmpty(expectedPartDiscriminators);

        JsonObject defs = GetDefs();
        JsonObject eventDef = (JsonObject)defs["event"]!;
        JsonArray anyOf = (JsonArray)eventDef["anyOf"]!;

        // Find session.getMessagesResult.
        JsonObject? getMessagesResult = anyOf
            .OfType<JsonObject>()
            .FirstOrDefault(o =>
                o["properties"] is JsonObject p &&
                p["type"] is JsonObject t &&
                t["const"] is JsonValue c &&
                c.TryGetValue(out string? v) && v == "session.getMessagesResult");

        Assert.NotNull(getMessagesResult);

        // messages: array of message records; each message record has parts: array of Part.
        JsonObject messagesSchema = (JsonObject)getMessagesResult["properties"]!["messages"]!;
        JsonObject messagesItems  = (JsonObject)messagesSchema["items"]!;
        JsonArray  messageAnyOf   = (JsonArray)messagesItems["anyOf"]!;

        // Find the "message" variant (not "compaction").
        JsonObject? messageVariant = messageAnyOf
            .OfType<JsonObject>()
            .FirstOrDefault(o =>
                o["properties"] is JsonObject p &&
                p["type"] is JsonObject t &&
                t["const"] is JsonValue c &&
                c.TryGetValue(out string? v) && v == "message");

        Assert.NotNull(messageVariant);

        JsonObject partsSchema = (JsonObject)messageVariant["properties"]!["parts"]!;
        JsonObject partsItems  = (JsonObject)partsSchema["items"]!;
        JsonArray  partsAnyOf  = (JsonArray)partsItems["anyOf"]!;

        HashSet<string> schemaPartTypes = ExtractTypeConstValues(partsAnyOf);

        IEnumerable<string> missing = expectedPartDiscriminators.Except(schemaPartTypes);
        Assert.Empty(missing);
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

        // Microsoft.Extensions.AI type names must not appear.
        Assert.DoesNotContain("Microsoft",              json, StringComparison.Ordinal);
        Assert.DoesNotContain("Extensions",             json, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatMessage",            json, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent",              json, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerOptions",  json, StringComparison.Ordinal);
        // Additional M.E.AI type names that must not leak.
        Assert.DoesNotContain("ChatClientMetadata",     json, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatCompletion",         json, StringComparison.Ordinal);
        Assert.DoesNotContain("FunctionCallContent",    json, StringComparison.Ordinal);
        Assert.DoesNotContain("FunctionResultContent",  json, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageContent",           json, StringComparison.Ordinal);
        Assert.DoesNotContain("UsageContent",           json, StringComparison.Ordinal);
        Assert.DoesNotContain("TextContent",            json, StringComparison.Ordinal);
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

    // ── reflection helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all string discriminator values declared via <c>[JsonDerivedType(..., typeDiscriminator: "...")]</c>
    /// on <paramref name="baseType"/>. The returned set is derived from the live assembly, so adding or
    /// removing a <c>[JsonDerivedType]</c> attribute will change the result immediately.
    /// </summary>
    private static IReadOnlySet<string> GetJsonDerivedTypeDiscriminators(Type baseType)
    {
        JsonDerivedTypeAttribute[] attributes =
            (JsonDerivedTypeAttribute[])Attribute.GetCustomAttributes(
                baseType,
                typeof(JsonDerivedTypeAttribute));

        HashSet<string> discriminators = new(StringComparer.Ordinal);
        foreach (JsonDerivedTypeAttribute attr in attributes)
        {
            if (attr.TypeDiscriminator is string s)
                discriminators.Add(s);
        }

        return discriminators;
    }

    /// <summary>
    /// Extracts all <c>properties.type.const</c> string values from an <c>anyOf</c> array.
    /// </summary>
    private static HashSet<string> ExtractTypeConstValues(JsonArray anyOf)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (JsonObject item in anyOf.OfType<JsonObject>())
        {
            if (item["properties"] is JsonObject props &&
                props["type"] is JsonObject typeProp &&
                typeProp["const"] is JsonValue constVal &&
                constVal.TryGetValue(out string? disc))
            {
                result.Add(disc);
            }
        }
        return result;
    }
}
