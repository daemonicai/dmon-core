using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Dmon.Protocol.Conversation;

namespace Dmon.Protocol.Tests;

/// <summary>
/// Verifies that <see cref="SessionLogLine"/> and <see cref="Part"/> export a well-formed
/// JSON schema that contains no third-party type names — the core guarantee of ADR-016.
/// </summary>
public sealed class ConversationRecordSchemaTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // ── export succeeds (no throw) ───────────────────────────────────────────

    [Fact]
    public void SessionLogLine_SchemaExport_Succeeds()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(SessionLogLine));
        Assert.NotNull(schema);
    }

    [Fact]
    public void Part_SchemaExport_Succeeds()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(Part));
        Assert.NotNull(schema);
    }

    // ── no third-party type names in the schema ──────────────────────────────

    [Fact]
    public void SessionLogLine_Schema_ContainsNoThirdPartyTypeNames()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(SessionLogLine));
        string schemaJson = schema.ToJsonString();

        Assert.DoesNotContain("Microsoft.Extensions.AI", schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatMessage",             schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent",               schemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Part_Schema_ContainsNoThirdPartyTypeNames()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(Part));
        string schemaJson = schema.ToJsonString();

        Assert.DoesNotContain("Microsoft.Extensions.AI", schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatMessage",             schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AIContent",               schemaJson, StringComparison.Ordinal);
    }

    // ── polymorphic discriminators are described ─────────────────────────────

    [Fact]
    public void SessionLogLine_Schema_DescribesMessageAndCompactionDiscriminators()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(SessionLogLine));
        string schemaJson = schema.ToJsonString();

        Assert.Contains("\"message\"",     schemaJson, StringComparison.Ordinal);
        Assert.Contains("\"compaction\"",  schemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Part_Schema_DescribesToolCallAndUnknownDiscriminators()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, typeof(Part));
        string schemaJson = schema.ToJsonString();

        Assert.Contains("\"toolCall\"",  schemaJson, StringComparison.Ordinal);
        Assert.Contains("\"unknown\"",   schemaJson, StringComparison.Ordinal);
    }
}
