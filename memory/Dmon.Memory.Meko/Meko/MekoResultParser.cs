using System.Text.Json;
using Dmon.Abstractions.Memory;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Parses raw <see cref="CallToolResult"/> values returned by Meko MCP tools into
/// <see cref="MemoryHit"/> records (D13). All <c>CallToolResult</c> handling is
/// confined here — nothing outside this class inspects a <c>CallToolResult</c>
/// or <c>ContentBlock</c> directly.
/// </summary>
/// <remarks>
/// Meko's output is tuned for LLM consumption (loose structure, early-access).
/// Assumed JSON shape (mem0-style, task 5.2 — verify on Discord):
/// <code>
/// { "results": [ { "id": "…", "memory": "…", "score": 0.9,
///                  "metadata": {…}, "relations": [ { "source": "…", "relation": "…", "target": "…" } ] } ] }
/// </code>
/// For single-item responses (get/update/delete) the shape is assumed to be one object at
/// the top level or a single-element results array. Missing/extra fields are non-fatal.
/// </remarks>
internal static class MekoResultParser
{
    // Field name assumptions — all live here so a single edit adjusts all mappings.
    // Verify on Discord (task 5.2) and update only these constants.
    private const string FieldResults = "results";
    private const string FieldMemory = "memory";   // primary text field (mem0 style)
    private const string FieldText = "text";        // fallback text field
    private const string FieldId = "id";
    private const string FieldScore = "score";
    private const string FieldMetadata = "metadata";
    private const string FieldRelations = "relations";
    private const string FieldRelSource = "source";
    private const string FieldRelRelation = "relation";
    private const string FieldRelTarget = "target";

    /// <summary>
    /// Extracts a list of <see cref="MemoryHit"/> records from a Meko tool result.
    /// Returns an empty list on any parse failure — never throws.
    /// </summary>
    public static IReadOnlyList<MemoryHit> ParseHits(
        CallToolResult result,
        ILogger logger)
    {
        string? raw = ExtractText(result);
        if (raw is null)
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            // Top-level "results" array (mem0/Meko assumed shape).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(FieldResults, out JsonElement resultsEl) &&
                resultsEl.ValueKind == JsonValueKind.Array)
            {
                return ParseHitsFromArray(resultsEl, logger);
            }

            // Single-item object at root.
            if (root.ValueKind == JsonValueKind.Object)
            {
                MemoryHit? single = TryParseHit(root, logger);
                return single is not null ? [single] : [];
            }

            // Bare array at root.
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseHitsFromArray(root, logger);
            }

            return [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MekoResultParser: failed to parse JSON from Meko tool result; degrading to empty.");
            return [];
        }
    }

    /// <summary>
    /// Extracts a single <see cref="MemoryHit"/> from a Meko <c>memory_get_by_id</c>
    /// result. Returns <see langword="null"/> on parse failure — never throws.
    /// </summary>
    public static MemoryHit? ParseSingleHit(CallToolResult result, ILogger logger)
    {
        string? raw = ExtractText(result);
        if (raw is null)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(FieldResults, out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    return TryParseHit(el, logger);
                }
                return null;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                return TryParseHit(root, logger);
            }

            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MekoResultParser: failed to parse single-hit JSON from Meko; returning null.");
            return null;
        }
    }

    // --- private helpers ---

    /// <summary>
    /// Concatenates the text of all <see cref="TextContentBlock"/> items in the result.
    /// Returns <see langword="null"/> when there is no text content.
    /// All <c>ContentBlock</c> handling is confined here.
    /// </summary>
    private static string? ExtractText(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
        {
            return null;
        }

        var parts = new System.Text.StringBuilder();
        foreach (ContentBlock block in result.Content)
        {
            if (block is TextContentBlock textBlock && textBlock.Text is not null)
            {
                parts.Append(textBlock.Text);
            }
        }

        string text = parts.ToString().Trim();
        return text.Length > 0 ? text : null;
    }

    private static IReadOnlyList<MemoryHit> ParseHitsFromArray(JsonElement array, ILogger logger)
    {
        var hits = new List<MemoryHit>();
        foreach (JsonElement el in array.EnumerateArray())
        {
            MemoryHit? hit = TryParseHit(el, logger);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }
        return hits;
    }

    private static MemoryHit? TryParseHit(JsonElement el, ILogger logger)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // id
        string? id = el.TryGetProperty(FieldId, out JsonElement idEl)
            ? idEl.GetString()
            : null;

        // text: prefer "memory", fall back to "text"
        string? text = null;
        if (el.TryGetProperty(FieldMemory, out JsonElement memEl))
        {
            text = memEl.GetString();
        }
        if (text is null && el.TryGetProperty(FieldText, out JsonElement textEl))
        {
            text = textEl.GetString();
        }

        if (id is null || text is null)
        {
            logger.LogDebug("MekoResultParser: skipping hit with missing id or text.");
            return null;
        }

        // score
        double score = el.TryGetProperty(FieldScore, out JsonElement scoreEl) &&
                       scoreEl.ValueKind == JsonValueKind.Number &&
                       scoreEl.TryGetDouble(out double s)
            ? s
            : 0.0;

        // metadata — all extra fields (including metadata sub-object if present)
        Dictionary<string, JsonElement>? metadata = null;
        if (el.TryGetProperty(FieldMetadata, out JsonElement metaEl) &&
            metaEl.ValueKind == JsonValueKind.Object)
        {
            metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in metaEl.EnumerateObject())
            {
                metadata[prop.Name] = prop.Value.Clone();
            }
        }

        // relations — Meko AGE graph edges
        IReadOnlyList<MemoryRelation>? relations = null;
        if (el.TryGetProperty(FieldRelations, out JsonElement relsEl) &&
            relsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<MemoryRelation>();
            foreach (JsonElement rel in relsEl.EnumerateArray())
            {
                MemoryRelation? r = TryParseRelation(rel, logger);
                if (r is not null)
                {
                    list.Add(r);
                }
            }
            if (list.Count > 0)
            {
                relations = list;
            }
        }

        return new MemoryHit(
            Id: id,
            Text: text,
            Source: MemorySource.LongTerm,
            Score: score,
            Metadata: metadata,
            Relations: relations);
    }

    private static MemoryRelation? TryParseRelation(JsonElement el, ILogger logger)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? src = el.TryGetProperty(FieldRelSource, out JsonElement se) ? se.GetString() : null;
        string? relation = el.TryGetProperty(FieldRelRelation, out JsonElement re) ? re.GetString() : null;
        string? target = el.TryGetProperty(FieldRelTarget, out JsonElement te) ? te.GetString() : null;

        if (src is null || relation is null || target is null)
        {
            logger.LogDebug("MekoResultParser: skipping relation with missing source/relation/target.");
            return null;
        }

        return new MemoryRelation(Source: src, Relation: relation, Target: target);
    }
}
