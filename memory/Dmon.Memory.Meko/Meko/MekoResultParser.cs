using System.Text.Json;
using Dmon.Abstractions.Memory;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Parses raw <see cref="CallToolResult"/> values returned by Meko MCP tools into
/// <see cref="MemoryHit"/> records. All <c>CallToolResult</c> handling is confined here —
/// nothing outside this class inspects a <c>CallToolResult</c> or <c>ContentBlock</c>
/// directly.
/// </summary>
/// <remarks>
/// Real response envelopes (live-captured 2026-05-29):
/// <para><b>memory_search / memory_get_all:</b>
/// <c>{ "results": [ { "id": "…", "memory": "…", "score": 0.9, "metadata": {…} } ],
///   "relations": [ { "source": "…", "relationship": "…", "target": "…" } ],
///   "promoted_relations": [ … ] }</c></para>
/// <para><b>memory_add:</b>
/// <c>{ "results": [ { "id": "…", "memory": "…", "event": "ADD" } ],
///   "relations": { "added_entities": …, "graph_nodes": …, "deleted_entities": … } }</c>
/// Note: <c>relations</c> is an <em>object</em> on add but an <em>array</em> on search —
/// handle both; never throw.</para>
/// <para><b>conversation_create:</b>
/// <c>{ "id": "…", "agent_id": "…", "title": "…", … }</c></para>
/// Missing/extra fields are non-fatal.
/// </remarks>
internal static class MekoResultParser
{
    // Field name constants — the single source of truth for all Meko result-side mappings.
    // Adjust only here when Meko changes its wire format.
    private const string FieldResults = "results";
    private const string FieldMemory = "memory";         // primary text field (mem0/Meko style)
    private const string FieldText = "text";              // fallback text field
    private const string FieldId = "id";
    private const string FieldScore = "score";
    private const string FieldMetadata = "metadata";
    private const string FieldRelations = "relations";    // top-level array (search) or object (add)
    private const string FieldRelSource = "source";
    private const string FieldRelRelationship = "relationship"; // wire predicate field name (NOT "relation")
    private const string FieldRelTarget = "target";

    /// <summary>
    /// Extracts a list of <see cref="MemoryHit"/> records from a Meko tool result.
    /// Reads <c>TextContentBlock.Text</c> as JSON; falls back to unwrapping
    /// <c>StructuredContent.result</c> when no text block is present.
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

            // Top-level "results" array — the canonical Meko/mem0 shape.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(FieldResults, out JsonElement resultsEl) &&
                resultsEl.ValueKind == JsonValueKind.Array)
            {
                // Top-level "relations" array (search path) — attach to all hits.
                IReadOnlyList<MemoryRelation>? topLevelRelations = null;
                if (root.TryGetProperty(FieldRelations, out JsonElement relationsEl) &&
                    relationsEl.ValueKind == JsonValueKind.Array)
                {
                    topLevelRelations = ParseRelationsFromArray(relationsEl, logger);
                }
                // relations is an object on the add path — silently ignore (no relations to attach).

                return ParseHitsFromArray(resultsEl, topLevelRelations, logger);
            }

            // Single-item object at root (e.g. memory_get_by_id returning bare object).
            if (root.ValueKind == JsonValueKind.Object)
            {
                MemoryHit? single = TryParseHit(root, relations: null, logger);
                return single is not null ? [single] : [];
            }

            // Bare array at root.
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseHitsFromArray(root, topLevelRelations: null, logger);
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
                    return TryParseHit(el, relations: null, logger);
                }
                return null;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                return TryParseHit(root, relations: null, logger);
            }

            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MekoResultParser: failed to parse single-hit JSON from Meko; returning null.");
            return null;
        }
    }

    /// <summary>
    /// Extracts the created memory id from a <c>memory_add</c> response
    /// (<c>results[0].id</c>). Returns <see langword="null"/> when absent or on
    /// parse failure — never throws.
    /// </summary>
    public static string? ParseAddedId(CallToolResult result, ILogger logger)
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
                root.TryGetProperty(FieldResults, out JsonElement resultsEl) &&
                resultsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in resultsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty(FieldId, out JsonElement idEl))
                    {
                        return idEl.GetString();
                    }
                }
            }

            return null;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "MekoResultParser: failed to parse memory_add id; returning null.");
            return null;
        }
    }

    // --- private helpers ---

    /// <summary>
    /// Extracts JSON text from the result.
    /// Prefers the first non-empty <see cref="TextContentBlock.Text"/>.
    /// Falls back to unwrapping <c>StructuredContent.result</c> (a JSON string) when
    /// no text block is present — Meko sometimes double-encodes responses this way.
    /// Returns <see langword="null"/> when no text can be extracted.
    /// All <c>ContentBlock</c> and <c>StructuredContent</c> handling is confined here.
    /// </summary>
    private static string? ExtractText(CallToolResult result)
    {
        if (result.Content is not null && result.Content.Count > 0)
        {
            var parts = new System.Text.StringBuilder();
            foreach (ContentBlock block in result.Content)
            {
                if (block is TextContentBlock textBlock && textBlock.Text is not null)
                {
                    parts.Append(textBlock.Text);
                }
            }

            string text = parts.ToString().Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        // Fallback: StructuredContent envelope { "result": "<json string>" }.
        if (result.StructuredContent.HasValue)
        {
            try
            {
                JsonElement sc = result.StructuredContent.Value;
                if (sc.ValueKind == JsonValueKind.Object &&
                    sc.TryGetProperty("result", out JsonElement resultEl) &&
                    resultEl.ValueKind == JsonValueKind.String)
                {
                    string? unwrapped = resultEl.GetString();
                    if (!string.IsNullOrWhiteSpace(unwrapped))
                    {
                        return unwrapped;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // StructuredContent was not a valid JSON object — fall through.
            }
        }

        return null;
    }

    private static IReadOnlyList<MemoryHit> ParseHitsFromArray(
        JsonElement array,
        IReadOnlyList<MemoryRelation>? topLevelRelations,
        ILogger logger)
    {
        var hits = new List<MemoryHit>();
        foreach (JsonElement el in array.EnumerateArray())
        {
            MemoryHit? hit = TryParseHit(el, topLevelRelations, logger);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }
        return hits;
    }

    private static MemoryHit? TryParseHit(
        JsonElement el,
        IReadOnlyList<MemoryRelation>? relations,
        ILogger logger)
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

        // metadata — from the per-item metadata sub-object if present
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

        return new MemoryHit(
            Id: id,
            Text: text,
            Source: MemorySource.LongTerm,
            Score: score,
            Metadata: metadata,
            Relations: relations is { Count: > 0 } ? relations : null);
    }

    private static IReadOnlyList<MemoryRelation> ParseRelationsFromArray(
        JsonElement array,
        ILogger logger)
    {
        var list = new List<MemoryRelation>();
        foreach (JsonElement rel in array.EnumerateArray())
        {
            MemoryRelation? r = TryParseRelation(rel, logger);
            if (r is not null)
            {
                list.Add(r);
            }
        }
        return list;
    }

    private static MemoryRelation? TryParseRelation(JsonElement el, ILogger logger)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? src = el.TryGetProperty(FieldRelSource, out JsonElement se) ? se.GetString() : null;
        // Wire field is "relationship" (not "relation") — confirmed from live response.
        string? relationship = el.TryGetProperty(FieldRelRelationship, out JsonElement re) ? re.GetString() : null;
        string? target = el.TryGetProperty(FieldRelTarget, out JsonElement te) ? te.GetString() : null;

        if (src is null || relationship is null || target is null)
        {
            logger.LogDebug("MekoResultParser: skipping relation with missing source/relationship/target.");
            return null;
        }

        return new MemoryRelation(Source: src, Relation: relationship, Target: target);
    }
}
