using System.Text.Json;
using Dmon.Abstractions.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Meko-backed <see cref="ILongTermMemory"/> over <see cref="IMekoToolInvoker"/>.
/// Maps each interface method to a Meko MCP <c>memory_*</c> tool (D7).
/// Uses <c>conversation_create</c> once per session to obtain the required
/// <c>conversation_id</c> UUID; all other <c>conversation_*</c> tools are never called.
/// </summary>
/// <remarks>
/// Ambient identity (<c>agent_id</c>, cached Meko <c>conversation_id</c>) is included in
/// every tool call. <c>scope = "admin"</c> is the fixed constant (D9).
/// <c>run_id</c> is set only for <see cref="MemoryScope.Session"/> calls (D9).
/// <c>datapack_id</c> is included only when <see cref="MekoLongTermOptions.DatapackId"/>
/// is a valid UUID (D9).
/// Meko arg/result field names live in <see cref="MekoArgNames"/> and
/// <see cref="MekoResultParser"/> — adjust there only.
/// </remarks>
internal sealed class MekoLongTermMemory : ILongTermMemory
{
    // All Meko tool arg-name strings — the single source of truth (6.4).
    private static class MekoArgNames
    {
        // Identity (ambient — always included)
        public const string DatapackId = "datapack_id";
        public const string AgentId = "agent_id";
        public const string ConversationId = "conversation_id";
        public const string RunId = "run_id";

        // Fixed scope constant (D9)
        public const string Scope = "scope";

        // memory_add (text path)
        public const string Text = "text";

        // memory_add (messages path) — JSON-serialized string (6.4)
        public const string Messages = "messages";

        // memory_search
        public const string Query = "query";
        public const string Limit = "limit";

        // memory_get_by_id / memory_update / memory_delete_by_id
        public const string MemoryId = "memory_id";

        // UpdateText intentionally maps to the same wire name as Text.
        // Kept separate so they can diverge without hunting call sites.
        public const string UpdateText = "text";

        // conversation_create — confirmed arg name (schema: title is optional human-readable label).
        public const string ConversationTitle = "title";
    }

    // Tool name constants — D7.
    private static class MekoTools
    {
        public const string ConversationCreate = "conversation_create";
        public const string Add = "memory_add";
        public const string Search = "memory_search";
        public const string GetById = "memory_get_by_id";
        public const string GetAll = "memory_get_all";
        public const string Update = "memory_update";
        public const string DeleteById = "memory_delete_by_id";
        public const string Flush = "flush_pending_memory_candidates";
    }

    // Candidate field names for the conversation UUID in the conversation_create response.
    // Tried at the root and under "data"/"result" envelopes; also handles TextContentBlock
    // whose text is JSON. Live-verified: "id" is the real field name.
    private static readonly string[] ConversationIdFieldCandidates =
    [
        "id",
        "conversation_id",
        "uuid",
        "conversation",
        "conversationId",
    ];

    private readonly IMekoToolInvoker _invoker;
    private readonly MekoMemoryContext _context;
    private readonly MekoLongTermOptions _options;
    private readonly ILogger<MekoLongTermMemory> _logger;

    // Lazy per-session Meko conversation UUID obtained via conversation_create (6.2).
    // null = not yet created; the semaphore ensures only one concurrent create call.
    private string? _mekoConversationId;
    private readonly SemaphoreSlim _conversationInitLock = new(1, 1);

    public MekoLongTermMemory(
        IMekoToolInvoker invoker,
        MekoMemoryContext context,
        MekoLongTermOptions options,
        ILogger<MekoLongTermMemory> logger)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _invoker = invoker;
        _context = context;
        _options = options;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // IMemoryStore
    // -----------------------------------------------------------------------

    /// <summary>
    /// Forwards turns to Meko via <c>memory_add(messages)</c> (JSON-string) when the
    /// configured <see cref="MekoCaptureMode"/> allows it. If the policy keeps nothing,
    /// this method completes successfully without any network call (D8).
    /// </summary>
    public async Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        if (_options.CaptureMode == MekoCaptureMode.None)
        {
            return;
        }

        if (turns.Count == 0)
        {
            return;
        }

        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        // messages must be a JSON string (6.4): serialize the array.
        string messagesJson = SerializeMessages(turns);

        Dictionary<string, object?> args = BuildAmbientArgs(scope, conversationId);
        args[MekoArgNames.Messages] = messagesJson;

        await _invoker.CallToolAsync(MekoTools.Add, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildAmbientArgs(scope, conversationId);
        args[MekoArgNames.Query] = query;
        args[MekoArgNames.Limit] = limit;

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.Search, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseHits(result, _logger);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // ILongTermMemory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Asserts a fact directly via <c>memory_add(text)</c>. Always executes,
    /// independent of <see cref="MekoCaptureMode"/> (D8).
    /// </summary>
    public async Task AddFactAsync(
        string fact,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildAmbientArgs(scope, conversationId);
        args[MekoArgNames.Text] = fact;

        await _invoker.CallToolAsync(MekoTools.Add, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryHit?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildIdArgs(conversationId);
        args[MekoArgNames.MemoryId] = id;

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.GetById, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseSingleHit(result, _logger);
    }

    public async Task<IReadOnlyList<MemoryHit>> ListAsync(
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildAmbientArgs(scope, conversationId);

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.GetAll, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseHits(result, _logger);
    }

    public async Task UpdateAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildIdArgs(conversationId);
        args[MekoArgNames.MemoryId] = id;
        args[MekoArgNames.UpdateText] = text;

        await _invoker.CallToolAsync(MekoTools.Update, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> args = BuildIdArgs(conversationId);
        args[MekoArgNames.MemoryId] = id;

        await _invoker.CallToolAsync(MekoTools.DeleteById, args, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // EnsureConversationAsync — lazy once-per-session conversation_create (6.2)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the cached Meko conversation UUID, calling <c>conversation_create</c>
    /// at most once per session. Thread-safe (semaphore-guarded double-checked).
    /// </summary>
    private async Task<string> EnsureConversationAsync(CancellationToken cancellationToken)
    {
        // Fast path — already initialised.
        if (_mekoConversationId is not null)
        {
            return _mekoConversationId;
        }

        await _conversationInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock (double-checked locking).
            if (_mekoConversationId is not null)
            {
                return _mekoConversationId;
            }

            _mekoConversationId = await CreateConversationAsync(cancellationToken).ConfigureAwait(false);
            return _mekoConversationId;
        }
        finally
        {
            _conversationInitLock.Release();
        }
    }

    /// <summary>
    /// Calls <c>conversation_create</c> and parses the returned conversation UUID.
    /// Args: scope="admin", agent_id, optional title (human-readable hint), optional datapack_id.
    /// Throws <see cref="InvalidOperationException"/> when the UUID cannot be extracted — includes
    /// the raw response text so the caller can diagnose the real field name.
    /// </summary>
    private async Task<string> CreateConversationAsync(CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.Scope] = MekoScopeMapping.AdminScope,
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationTitle] = $"dmon-session-{_context.Context.ConversationId}",
        };

        if (IsUuid(_context.Context.DatapackId))
        {
            args[MekoArgNames.DatapackId] = _context.Context.DatapackId;
        }

        CallToolResult result = await _invoker.CallToolAsync(
            MekoTools.ConversationCreate, args, cancellationToken).ConfigureAwait(false);

        string? conversationId = TryParseConversationId(result, out string rawResponse);
        if (conversationId is not null)
        {
            _logger.LogDebug(
                "MekoLongTermMemory: conversation_create returned conversation UUID {Id}.",
                conversationId);
            return conversationId;
        }

        throw new InvalidOperationException(
            $"MekoLongTermMemory: could not parse conversation UUID from conversation_create response. " +
            $"Tried candidates: [{string.Join(", ", ConversationIdFieldCandidates)}] at root, " +
            $"under 'data', and under 'result'. Raw response: {rawResponse}");
    }

    /// <summary>
    /// Parses the conversation UUID from a <c>conversation_create</c> result.
    /// Tries <see cref="ConversationIdFieldCandidates"/> in order at: root, <c>"data"</c>
    /// envelope, and <c>"result"</c> envelope. Also handles the case where the value is
    /// inside a <c>TextContentBlock</c> whose text is itself JSON (Meko LLM-tuned output).
    /// Sets <paramref name="rawResponse"/> to the concatenated block text (for diagnostics).
    /// Returns <see langword="null"/> when no candidate matches.
    /// </summary>
    private static string? TryParseConversationId(CallToolResult result, out string rawResponse)
    {
        if (result.Content is null)
        {
            rawResponse = "(null content)";
            return null;
        }

        var rawParts = new System.Text.StringBuilder();

        foreach (ContentBlock block in result.Content)
        {
            if (block is not TextContentBlock tb || tb.Text is null)
            {
                continue;
            }

            rawParts.AppendLine(tb.Text);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(tb.Text);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? hit = TryExtractFromObject(root);
                if (hit is not null)
                {
                    rawResponse = rawParts.ToString();
                    return hit;
                }

                // Try { "data": { ... } } envelope.
                if (root.TryGetProperty("data", out JsonElement dataEl) &&
                    dataEl.ValueKind == JsonValueKind.Object)
                {
                    hit = TryExtractFromObject(dataEl);
                    if (hit is not null)
                    {
                        rawResponse = rawParts.ToString();
                        return hit;
                    }
                }

                // Try { "result": { ... } } envelope.
                if (root.TryGetProperty("result", out JsonElement resultEl) &&
                    resultEl.ValueKind == JsonValueKind.Object)
                {
                    hit = TryExtractFromObject(resultEl);
                    if (hit is not null)
                    {
                        rawResponse = rawParts.ToString();
                        return hit;
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON — try the next block.
            }
        }

        rawResponse = rawParts.Length > 0 ? rawParts.ToString() : "(no text blocks)";
        return null;
    }

    private static string? TryExtractFromObject(JsonElement obj)
    {
        foreach (string field in ConversationIdFieldCandidates)
        {
            if (obj.TryGetProperty(field, out JsonElement el) &&
                el.ValueKind == JsonValueKind.String)
            {
                string? value = el.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the base argument dictionary with ambient identity, the fixed
    /// <c>scope="admin"</c> constant, and optionally <c>run_id</c> (D9).
    /// <c>datapack_id</c> is included only when it is a valid UUID (D9).
    /// </summary>
    private Dictionary<string, object?> BuildAmbientArgs(MemoryScope scope, string mekoConversationId)
    {
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = mekoConversationId,
            [MekoArgNames.Scope] = MekoScopeMapping.AdminScope,
        };

        string? runId = MekoScopeMapping.ToRunId(scope, _context.Context.ConversationId);
        if (runId is not null)
        {
            args[MekoArgNames.RunId] = runId;
        }

        if (IsUuid(_context.Context.DatapackId))
        {
            args[MekoArgNames.DatapackId] = _context.Context.DatapackId;
        }

        return args;
    }

    /// <summary>
    /// Builds identity args for single-id operations (no scope/run_id needed).
    /// </summary>
    private Dictionary<string, object?> BuildIdArgs(string mekoConversationId)
    {
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = mekoConversationId,
            [MekoArgNames.Scope] = MekoScopeMapping.AdminScope,
        };

        if (IsUuid(_context.Context.DatapackId))
        {
            args[MekoArgNames.DatapackId] = _context.Context.DatapackId;
        }

        return args;
    }

    /// <summary>
    /// Serializes a list of <see cref="ChatMessage"/> turns to the JSON string that
    /// Meko's <c>memory_add.messages</c> arg expects (6.4).
    /// Format: <c>[{"role":"user","content":"..."},...]</c>
    /// </summary>
    private static string SerializeMessages(IReadOnlyList<ChatMessage> turns)
    {
        var items = turns.Select(t => new Dictionary<string, string>
        {
            ["role"] = t.Role.Value,
            ["content"] = t.Text ?? string.Empty,
        });
        return JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a well-formed UUID
    /// (Guid). Non-UUID strings (e.g. human-readable workspace names) return false.
    /// </summary>
    private static bool IsUuid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out _);

    private async Task FlushInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            string conversationId = await EnsureConversationAsync(cancellationToken).ConfigureAwait(false);

            var args = new Dictionary<string, object?>
            {
                [MekoArgNames.AgentId] = _context.Context.AgentId,
                [MekoArgNames.ConversationId] = conversationId,
                [MekoArgNames.Scope] = MekoScopeMapping.AdminScope,
            };

            if (IsUuid(_context.Context.DatapackId))
            {
                args[MekoArgNames.DatapackId] = _context.Context.DatapackId;
            }

            CallToolResult result = await _invoker.CallToolAsync(MekoTools.Flush, args, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("MekoLongTermMemory: flush completed (best-effort). Result block count: {Count}.",
                result.Content?.Count ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MekoLongTermMemory: flush_pending_memory_candidates failed (best-effort; suppressed).");
        }
    }
}
