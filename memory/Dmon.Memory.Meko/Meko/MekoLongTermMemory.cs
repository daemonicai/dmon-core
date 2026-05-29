using Dmon.Abstractions.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Dmon.Memory.Meko;

/// <summary>
/// Meko-backed <see cref="ILongTermMemory"/> over <see cref="IMekoToolInvoker"/>.
/// Maps each interface method to a Meko MCP <c>memory_*</c> tool (D7).
/// Never calls <c>conversation_*</c> or <c>knowledgebase_*</c> tools.
/// </summary>
/// <remarks>
/// Ambient identity (<c>datapack_id</c>, <c>agent_id</c>, <c>conversation_id</c>) is
/// injected from <see cref="MekoMemoryContext"/> once at construction and included in
/// every tool call. <c>run_id</c> is NEVER set (D9).
/// Meko arg/result field names are assumed defaults (task 5.1 / 5.2) — verify on Discord
/// and adjust <see cref="MekoArgNames"/> and <see cref="MekoResultParser"/> only.
/// </remarks>
internal sealed class MekoLongTermMemory : ILongTermMemory
{
    // Assumed Meko tool arg names (task 5.1/5.2 — verify on Discord; update only here).
    // All tool invocations go through BuildArgs() / the named methods below.
    private static class MekoArgNames
    {
        // Identity (ambient — always included, run_id deliberately absent per D9)
        public const string DatapackId = "datapack_id";
        public const string AgentId = "agent_id";
        public const string ConversationId = "conversation_id";

        // Per-call scope
        public const string Scope = "scope";

        // memory_add (text path)
        public const string Text = "text";

        // memory_add (messages path)
        public const string Messages = "messages";
        public const string Role = "role";
        public const string Content = "content";

        // memory_search
        public const string Query = "query";
        public const string Limit = "limit";

        // memory_get_by_id / memory_update / memory_delete_by_id
        public const string MemoryId = "memory_id";
        // UpdateText is intentionally the same string value as Text ("text") — both
        // map to the same Meko field name today. They are kept as separate constants
        // so either can diverge independently once Meko's schema is confirmed (task 5.1).
        public const string UpdateText = "text";
    }

    // Tool name constants — D7.
    private static class MekoTools
    {
        public const string Add = "memory_add";
        public const string Search = "memory_search";
        public const string GetById = "memory_get_by_id";
        public const string GetAll = "memory_get_all";
        public const string Update = "memory_update";
        public const string DeleteById = "memory_delete_by_id";
        public const string Flush = "flush_pending_memory_candidates";
    }

    private readonly IMekoToolInvoker _invoker;
    private readonly MekoMemoryContext _context;
    private readonly MekoLongTermOptions _options;
    private readonly ILogger<MekoLongTermMemory> _logger;

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
    /// Forwards turns to Meko via <c>memory_add(messages)</c> when the configured
    /// <see cref="MekoCaptureMode"/> allows it. If the policy keeps nothing, this
    /// method completes successfully without making any network call (D8).
    /// </summary>
    public async Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        if (_options.CaptureMode == MekoCaptureMode.None)
        {
            // Opt-in is off; complete successfully with no network call (D8).
            return;
        }

        if (turns.Count == 0)
        {
            // Nothing to record; avoid a wasted hosted call with an empty messages array.
            return;
        }

        var messages = turns.Select(t => (object?)new Dictionary<string, object?>
        {
            [MekoArgNames.Role] = t.Role.Value,
            [MekoArgNames.Content] = t.Text ?? string.Empty,
        }).ToList();

        Dictionary<string, object?> args = BuildAmbientArgs(scope);
        args[MekoArgNames.Messages] = messages;

        await _invoker.CallToolAsync(MekoTools.Add, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> args = BuildAmbientArgs(scope);
        args[MekoArgNames.Query] = query;
        args[MekoArgNames.Limit] = limit;

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.Search, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseHits(result, _logger);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        // Best-effort: FlushInternalAsync swallows non-cancellation exceptions so the caller
        // gets a deterministic barrier without surfacing flush failures. Cancellation IS
        // observable — OperationCanceledException propagates (D11).
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
        Dictionary<string, object?> args = BuildAmbientArgs(scope);
        args[MekoArgNames.Text] = fact;

        await _invoker.CallToolAsync(MekoTools.Add, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryHit?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        // Scope is not used for single-id lookup; include ambient identity only.
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.DatapackId] = _context.Context.DatapackId,
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = _context.Context.ConversationId,
            [MekoArgNames.MemoryId] = id,
        };

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.GetById, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseSingleHit(result, _logger);
    }

    public async Task<IReadOnlyList<MemoryHit>> ListAsync(
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> args = BuildAmbientArgs(scope);

        CallToolResult result = await _invoker.CallToolAsync(MekoTools.GetAll, args, cancellationToken).ConfigureAwait(false);
        return MekoResultParser.ParseHits(result, _logger);
    }

    public async Task UpdateAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.DatapackId] = _context.Context.DatapackId,
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = _context.Context.ConversationId,
            [MekoArgNames.MemoryId] = id,
            [MekoArgNames.UpdateText] = text,
        };

        await _invoker.CallToolAsync(MekoTools.Update, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            [MekoArgNames.DatapackId] = _context.Context.DatapackId,
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = _context.Context.ConversationId,
            [MekoArgNames.MemoryId] = id,
        };

        await _invoker.CallToolAsync(MekoTools.DeleteById, args, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the base argument dictionary with ambient identity + mapped scope.
    /// <c>run_id</c> is intentionally absent (D9).
    /// </summary>
    private Dictionary<string, object?> BuildAmbientArgs(MemoryScope scope) =>
        new()
        {
            [MekoArgNames.DatapackId] = _context.Context.DatapackId,
            [MekoArgNames.AgentId] = _context.Context.AgentId,
            [MekoArgNames.ConversationId] = _context.Context.ConversationId,
            [MekoArgNames.Scope] = MekoScopeMapping.ToMekoScope(scope),
        };

    private async Task FlushInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var args = new Dictionary<string, object?>
            {
                [MekoArgNames.DatapackId] = _context.Context.DatapackId,
                [MekoArgNames.AgentId] = _context.Context.AgentId,
                [MekoArgNames.ConversationId] = _context.Context.ConversationId,
            };

            CallToolResult result = await _invoker.CallToolAsync(MekoTools.Flush, args, cancellationToken).ConfigureAwait(false);

            // flush_pending_memory_candidates may return an agent-directive instructing
            // the caller to scan recent turns and call memory_add (task 5.1 — verify on
            // Discord). Full directive-acting is deferred until the semantics are confirmed;
            // for now, log at debug level and treat the call as best-effort.
            _logger.LogDebug("MekoLongTermMemory: flush completed (best-effort). Result block count: {Count}.",
                result.Content?.Count ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: never surface flush failures to the caller.
            _logger.LogWarning(ex, "MekoLongTermMemory: flush_pending_memory_candidates failed (best-effort; suppressed).");
        }
    }
}
