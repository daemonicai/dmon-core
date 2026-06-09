using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Events;
using Dmon.Protocol.Gateway;

namespace Dmon.Protocol;

/// <summary>
/// Generates a single JSON Schema document describing the full dmon wire-protocol surface:
/// the <see cref="Command"/> and <see cref="Event"/> polymorphic hierarchies, the
/// conversation <see cref="Part"/> union (ADR-016), and the gateway control-frame family
/// (ADR-012). The schema is generated from the live <see cref="WireSerializerOptions.Default"/>
/// instance so it reflects the bytes the runtime actually emits.
/// </summary>
public static class ProtocolSchemaExporter
{
    // The control-frame family uses a "gw" discriminator, not "type".
    // List them explicitly — there is no shared base type to export polymorphically.
    private static readonly IReadOnlyList<(string Key, Type FrameType)> ControlFrameTypes =
    [
        ("gw.attach",         typeof(AttachFrame)),
        ("gw.attached",       typeof(AttachedFrame)),
        ("gw.ack",            typeof(AckFrame)),
        ("gw.create",         typeof(CreateFrame)),
        ("gw.created",        typeof(CreatedFrame)),
        ("gw.createRejected", typeof(CreateRejectedFrame)),
        ("gw.ping",           typeof(PingFrame)),
        ("gw.pong",           typeof(PongFrame)),
    ];

    /// <summary>
    /// Exports the full wire-protocol schema as a <see cref="JsonNode"/>.
    /// The document is deterministically ordered so that repeated calls produce identical output.
    /// </summary>
    public static JsonNode Export()
    {
        JsonSerializerOptions options = WireSerializerOptions.Default;

        // Export each root hierarchy. JsonSchemaExporter.GetJsonSchemaAsNode emits a $defs-based
        // document for polymorphic types; we hoist all $defs into a single shared top-level $defs
        // map and reference them via $ref.
        JsonNode commandSchema  = JsonSchemaExporter.GetJsonSchemaAsNode(options, typeof(Command));
        JsonNode eventSchema    = JsonSchemaExporter.GetJsonSchemaAsNode(options, typeof(Event));
        JsonNode partSchema     = JsonSchemaExporter.GetJsonSchemaAsNode(options, typeof(Part));

        // Collect all $defs from the four families into a single sorted dictionary.
        // Sorting is critical for determinism: JsonSchemaExporter does not guarantee ordering.
        SortedDictionary<string, JsonNode> allDefs = new(StringComparer.Ordinal);

        HoistDefs(commandSchema,  "command",  allDefs);
        HoistDefs(eventSchema,    "event",    allDefs);
        HoistDefs(partSchema,     "part",     allDefs);

        // Control frames: export each individually, key them under their "gw.*" name.
        foreach ((string key, Type frameType) in ControlFrameTypes)
        {
            JsonNode frameSchema = JsonSchemaExporter.GetJsonSchemaAsNode(options, frameType);
            // Remove inline $defs if any (unlikely for simple flat records, but defensive).
            HoistDefs(frameSchema, key, allDefs);
            // Flatten the frame schema itself into $defs under its key, then fix up the
            // "gw" property: the exporter emits it as {"type":["string","null"]} because the
            // C# property is a get-only computed string, but on the wire it is always a
            // literal constant. Replace it with {"const":"<value>"} and add "gw" to required.
            JsonNode stripped = StripDefs(frameSchema);
            string discriminatorValue = key["gw.".Length..]; // e.g. "gw.attach" → "attach"
            FixGwDiscriminator(stripped, discriminatorValue);
            allDefs[key] = stripped;
        }

        // Build the top-level oneOf entries as $ref into the shared $defs.
        JsonArray oneOf = new()
        {
            new JsonObject { ["$ref"] = "#/$defs/command" },
            new JsonObject { ["$ref"] = "#/$defs/event" },
            new JsonObject { ["$ref"] = "#/$defs/part" },
        };
        foreach ((string key, _) in ControlFrameTypes)
        {
            oneOf.Add(new JsonObject { ["$ref"] = $"#/$defs/{key}" });
        }

        // Add the command/event/part roots themselves into $defs (without their $defs sub-key).
        allDefs["command"] = StripDefs(commandSchema);
        allDefs["event"]   = StripDefs(eventSchema);
        allDefs["part"]    = StripDefs(partSchema);

        // Build the final $defs JsonObject in sorted key order.
        JsonObject defsNode = new();
        foreach (KeyValuePair<string, JsonNode> entry in allDefs)
        {
            defsNode[entry.Key] = entry.Value.DeepClone();
        }

        // Post-process the whole $defs tree: omitted-nullable properties must not be required.
        // JsonSchemaExporter ignores DefaultIgnoreCondition=WhenWritingNull, so it marks nullable
        // properties as required even though the runtime omits them. Walk the tree and remove
        // any property from "required" if its schema admits null (type includes "null" or is
        // {"type":["T","null"]}).
        PruneOmittedNullablesFromRequired(defsNode);

        JsonObject root = new()
        {
            ["$schema"]           = "https://json-schema.org/draft/2020-12/schema",
            ["x-protocolVersion"] = ProtocolVersion.Current,
            ["description"]       = "dmon wire protocol: all client-visible frame types.",
            ["oneOf"]             = oneOf,
            ["$defs"]             = defsNode,
        };

        return root;
    }

    /// <summary>
    /// Serializes the exported schema to an indented JSON string with a trailing newline,
    /// suitable for writing to <c>docs/protocol/schema.json</c>.
    /// </summary>
    public static string ExportAsJson()
    {
        JsonNode schema = Export();
        string json = schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return json + "\n";
    }

    // Moves all entries from a schema's "$defs" node into the shared allDefs map,
    // prefixing their names with a namespace prefix to avoid collisions across families.
    // The original schema's "$defs" node is left in place (StripDefs removes it when needed).
    private static void HoistDefs(
        JsonNode schema,
        string prefix,
        SortedDictionary<string, JsonNode> allDefs)
    {
        if (schema is not JsonObject obj)
            return;

        if (obj["$defs"] is not JsonObject defs)
            return;

        foreach (KeyValuePair<string, JsonNode?> kv in defs)
        {
            if (kv.Value is null)
                continue;

            string qualifiedKey = $"{prefix}.{kv.Key}";
            allDefs[qualifiedKey] = kv.Value.DeepClone();
        }

        // Rewrite $ref values within the schema to point at the hoisted (prefixed) keys.
        RewriteRefs(schema, prefix);
    }

    // Returns a deep clone of the schema with the top-level "$defs" key removed.
    private static JsonNode StripDefs(JsonNode schema)
    {
        JsonNode clone = schema.DeepClone();
        if (clone is JsonObject obj)
            obj.Remove("$defs");
        return clone;
    }

    // Walks the JSON tree and rewrites every "$ref" value that starts with "#/$defs/"
    // to use the namespaced prefix, so cross-references remain valid after hoisting.
    private static void RewriteRefs(JsonNode node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue refValue &&
                    refValue.TryGetValue(out string? refStr) &&
                    refStr is not null &&
                    refStr.StartsWith("#/$defs/", StringComparison.Ordinal))
                {
                    string defName = refStr["#/$defs/".Length..];
                    obj["$ref"] = $"#/$defs/{prefix}.{defName}";
                }
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (kv.Value is not null)
                        RewriteRefs(kv.Value, prefix);
                }
                break;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    if (item is not null)
                        RewriteRefs(item, prefix);
                }
                break;
        }
    }

    // Replaces the "gw" property schema with {"const":"<value>"} and ensures "gw" is in
    // the "required" array. The exporter emits the computed get-only string property as
    // {"type":["string","null"]}, which is wrong: it is always a specific literal on the wire.
    private static void FixGwDiscriminator(JsonNode schema, string discriminatorValue)
    {
        if (schema is not JsonObject obj)
            return;

        if (obj["properties"] is not JsonObject props)
            return;

        // Replace the "gw" property schema with a const.
        props["gw"] = new JsonObject { ["const"] = discriminatorValue };

        // Ensure "gw" is in the required array.
        JsonArray required = obj["required"] is JsonArray existing
            ? existing
            : new JsonArray();

        bool alreadyRequired = false;
        foreach (JsonNode? item in required)
        {
            if (item is JsonValue v && v.TryGetValue(out string? s) && s == "gw")
            {
                alreadyRequired = true;
                break;
            }
        }

        if (!alreadyRequired)
            required.Add("gw");

        obj["required"] = required;
    }

    // Removes nullable properties from "required" arrays at every level of a schema tree.
    // Rule: a property is "omit-when-null" when WhenWritingNull is active (which it is in
    // WireSerializerOptions.Default). JsonSchemaExporter ignores that setting and marks such
    // properties required anyway. A property is nullable when its schema is one of:
    //   {"type":["X","null",...]}  (type is an array containing "null")
    //   {"type":"null"}
    // Both forms are unambiguous; no other heuristic is applied.
    private static void PruneOmittedNullablesFromRequired(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            // If this object has both "properties" and "required", prune.
            if (obj["properties"] is JsonObject props && obj["required"] is JsonArray required)
            {
                List<string> toRemove = [];
                foreach (JsonNode? item in required)
                {
                    if (item is not JsonValue v || !v.TryGetValue(out string? propName) || propName is null)
                        continue;

                    if (props[propName] is JsonObject propSchema && IsNullableSchema(propSchema))
                        toRemove.Add(propName);
                }

                foreach (string name in toRemove)
                {
                    // Remove all occurrences of this name from the required array.
                    for (int i = required.Count - 1; i >= 0; i--)
                    {
                        if (required[i] is JsonValue v2 &&
                            v2.TryGetValue(out string? s) &&
                            s == name)
                        {
                            required.RemoveAt(i);
                        }
                    }
                }
            }

            // Recurse into all child nodes.
            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Value is not null)
                    PruneOmittedNullablesFromRequired(kv.Value);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item is not null)
                    PruneOmittedNullablesFromRequired(item);
            }
        }
    }

    // Returns true if the schema explicitly admits null values (type array contains "null",
    // or type is the string "null"). Does not check $ref-resolved schemas.
    private static bool IsNullableSchema(JsonObject schema)
    {
        JsonNode? typeNode = schema["type"];
        if (typeNode is null)
            return false;

        if (typeNode is JsonValue typeStr &&
            typeStr.TryGetValue(out string? singleType) &&
            singleType == "null")
        {
            return true;
        }

        if (typeNode is JsonArray typeArr)
        {
            foreach (JsonNode? item in typeArr)
            {
                if (item is JsonValue v && v.TryGetValue(out string? t) && t == "null")
                    return true;
            }
        }

        return false;
    }
}
