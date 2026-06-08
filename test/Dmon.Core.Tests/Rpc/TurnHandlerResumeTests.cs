using System.Runtime.CompilerServices;
using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Abstractions.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Tests for <see cref="TurnHandler.SeedHistoryFromSessionAsync"/> (task 5.1) and
/// the D6 post-persist reconciliation in <see cref="TurnHandler"/> (task 5.2).
/// </summary>
public sealed class TurnHandlerResumeTests
{
    // ── 5.1: seeding from session ────────────────────────────────────────────

    [Fact]
    public async Task SeedHistoryFromSession_RestoresUserAndAssistantMessages()
    {
        string sessionId = "session-seed-1";

        IReadOnlyList<SessionLogLine> records =
        [
            MakeMessage("user",      "What is the capital of France?"),
            MakeMessage("assistant", "The capital of France is Paris."),
        ];

        CapturingChatClient client = new("follow-up answer");
        SeededSessionStore store = new(sessionId, records);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        // Submit a follow-up turn and inspect what messages the provider received.
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "And Germany?" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // system + seeded user + seeded assistant + new user = 4
        Assert.Equal(4, sent.Count);
        Assert.Equal(ChatRole.System,    sent[0].Role);
        Assert.Equal(ChatRole.User,      sent[1].Role);
        Assert.Equal(ChatRole.Assistant, sent[2].Role);
        Assert.Equal(ChatRole.User,      sent[3].Role);

        string userText      = Assert.IsType<TextContent>(sent[1].Contents[0]).Text;
        string assistantText = Assert.IsType<TextContent>(sent[2].Contents[0]).Text;
        Assert.Equal("What is the capital of France?", userText);
        Assert.Equal("The capital of France is Paris.", assistantText);
    }

    [Fact]
    public async Task SeedHistoryFromSession_ToolCallAndResultContextRestored()
    {
        string sessionId = "session-seed-tc";
        string callId = "call-abc";

        MessageRecord toolCallMsg = new()
        {
            EntryId   = NewId(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     = [new ToolCallPart { CallId = callId, Name = "get_weather", Args = JsonSerializer.Deserialize<JsonElement>("{\"city\":\"Paris\"}") }]
        };

        MessageRecord toolResultMsg = new()
        {
            EntryId   = NewId(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "tool",
            Parts     = [new ToolResultPart { CallId = callId, Result = JsonSerializer.Deserialize<JsonElement>("\"Sunny, 22\\u00b0C\"") }]
        };

        IReadOnlyList<SessionLogLine> records = [toolCallMsg, toolResultMsg];

        CapturingChatClient client = new("done");
        SeededSessionStore store = new(sessionId, records);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r2", Message = "What about Berlin?" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // system + tool-call assistant + tool result + user = 4
        Assert.Equal(4, sent.Count);

        // Tool-call message reconstructed with FunctionCallContent.
        ChatMessage toolCallSent = sent[1];
        Assert.Equal(ChatRole.Assistant, toolCallSent.Role);
        FunctionCallContent fcContent = Assert.IsType<FunctionCallContent>(toolCallSent.Contents[0]);
        Assert.Equal("get_weather", fcContent.Name);
        Assert.Equal(callId, fcContent.CallId);

        // Tool result reconstructed with FunctionResultContent.
        ChatMessage toolResultSent = sent[2];
        FunctionResultContent frContent = Assert.IsType<FunctionResultContent>(toolResultSent.Contents[0]);
        Assert.Equal(callId, frContent.CallId);
    }

    [Fact]
    public async Task SeedHistoryFromSession_ReasoningAndUsageAndUnknown_ExcludedFromContext()
    {
        string sessionId = "session-seed-exc";

        MessageRecord msgWithExtras = new()
        {
            EntryId   = NewId(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = "assistant",
            Parts     =
            [
                new TextPart { Text = "Visible text." },
                new ReasoningPart { Text = "Hidden thinking." },
                new UsagePart(),
                new UnknownPart { Raw = JsonSerializer.Deserialize<JsonElement>("{\"x\":1}"), ProducedBy = "test" }
            ]
        };

        IReadOnlyList<SessionLogLine> records = [msgWithExtras];

        CapturingChatClient client = new("ok");
        SeededSessionStore store = new(sessionId, records);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r3", Message = "Hi" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // system + reconstructed assistant + user = 3
        Assert.Equal(3, sent.Count);

        ChatMessage reconstructed = sent[1];
        Assert.Equal(ChatRole.Assistant, reconstructed.Role);
        // Only the TextContent should be present — reasoning/usage/unknown excluded.
        Assert.Single(reconstructed.Contents);
        TextContent tc = Assert.IsType<TextContent>(reconstructed.Contents[0]);
        Assert.Equal("Visible text.", tc.Text);
    }

    [Fact]
    public async Task SeedHistoryFromSession_CompactionSummary_SeedsSyntheticAssistantMessage()
    {
        string sessionId = "session-seed-compact";

        CompactionMessage compaction = new()
        {
            EntryId       = NewId(),
            Timestamp     = DateTimeOffset.UtcNow,
            Summary       = "The user asked about European capitals. Assistant confirmed Paris, Berlin, Rome.",
            SupersedesUpTo = NewId(),
            Reason        = "manual",
            TokensBefore  = 500
        };

        // One post-compaction message.
        MessageRecord postCompaction = MakeMessage("user", "What about Madrid?");

        IReadOnlyList<SessionLogLine> records = [compaction, postCompaction];

        CapturingChatClient client = new("Madrid is the capital of Spain.");
        SeededSessionStore store = new(sessionId, records);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r4", Message = "And Lisbon?" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // system + compaction-summary assistant + seeded user + new user = 4
        Assert.Equal(4, sent.Count);
        Assert.Equal(ChatRole.System,    sent[0].Role);
        Assert.Equal(ChatRole.Assistant, sent[1].Role);
        Assert.Equal(ChatRole.User,      sent[2].Role);
        Assert.Equal(ChatRole.User,      sent[3].Role);

        string summaryText = Assert.IsType<TextContent>(sent[1].Contents[0]).Text;
        Assert.Equal(compaction.Summary, summaryText);
    }

    [Fact]
    public async Task SeedHistoryFromSession_EmptySession_LeavesHistoryClear()
    {
        string sessionId = "session-seed-empty";

        CapturingChatClient client = new("fresh start");
        SeededSessionStore store = new(sessionId, []);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r5", Message = "Hello" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // system + user only (no seeded context)
        Assert.Equal(2, sent.Count);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal(ChatRole.User,   sent[1].Role);
    }

    [Fact]
    public async Task SeedHistoryFromSession_NoSessionStore_IsNoOp()
    {
        CapturingChatClient client = new("response");
        // No session store wired.
        (TurnHandler handler, _) = TurnHandlerFactory.Create(client);

        // Should complete without throwing.
        await handler.SeedHistoryFromSessionAsync("any-session", CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r6", Message = "Hello" }, CancellationToken.None);

        // system + user only
        Assert.Equal(2, client.LastMessages.Count);
    }

    [Fact]
    public async Task SeedHistoryFromSession_PersistedCountSetCorrectly_NoReappendOnNextTurn()
    {
        string sessionId = "session-seed-norepeat";

        IReadOnlyList<SessionLogLine> records =
        [
            MakeMessage("user",      "Previously asked question"),
            MakeMessage("assistant", "Previously given answer"),
        ];

        CapturingChatClient client = new("new answer");
        RecordingSessionStore store = new(sessionId, records);
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        // Submit one new turn.
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r7", Message = "New question" }, CancellationToken.None);

        // Only the new turn's messages (user + assistant) should be persisted —
        // the seeded messages already existed on disk and must not be re-appended.
        IReadOnlyList<ChatMessage> appended = store.AppendedMessages;
        Assert.DoesNotContain(appended, m => m.Contents.OfType<TextContent>().Any(t => t.Text == "Previously asked question"));
        Assert.DoesNotContain(appended, m => m.Contents.OfType<TextContent>().Any(t => t.Text == "Previously given answer"));
        Assert.Contains(appended, m => m.Role == ChatRole.User && m.Contents.OfType<TextContent>().Any(t => t.Text == "New question"));
    }

    // ── 5.2: D6 post-persist reconciliation ─────────────────────────────────

    /// <summary>
    /// Verifies that an offloaded tool result (Result=null, AttachmentRef set) is skipped during
    /// seeding (produces an empty-content message which is excluded from history), and that the
    /// tool-call message preceding it is correctly restored in context.
    /// </summary>
    [Fact]
    public async Task Seed_OffloadedToolResult_SkippedAndToolCallRetained()
    {
        string sessionId = "session-d6";
        const string callId = "tool-call-d6";

        // Seed a prior session that includes an offloaded tool result (Result = null, AttachmentRef set).
        // ConversationMapper.ToMessage returns null for offloaded results, so that message gets
        // empty contents and is excluded from history — the tool-call message before it is retained.
        IReadOnlyList<SessionLogLine> priorRecords =
        [
            MakeMessage("user", "Run a tool"),
            new MessageRecord
            {
                EntryId   = NewId(),
                Timestamp = DateTimeOffset.UtcNow,
                Role      = "assistant",
                Parts     = [new ToolCallPart { CallId = callId, Name = "run_thing", Args = JsonSerializer.Deserialize<JsonElement>("{}") }]
            },
            new MessageRecord
            {
                EntryId   = NewId(),
                Timestamp = DateTimeOffset.UtcNow,
                Role      = "tool",
                // Offloaded: Result = null means ConversationMapper.ToMessage skips this entry.
                Parts     = [new ToolResultPart { CallId = callId, Result = null, AttachmentRef = "attachments/large.json" }]
            },
        ];

        SeededSessionStore seededStore = new(sessionId, priorRecords);
        CapturingChatClient client = new("seeded-response");
        ActiveSessionHandler sessionHandler = new(sessionId);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: seededStore);

        await handler.SeedHistoryFromSessionAsync(sessionId, CancellationToken.None);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r8", Message = "What happened?" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // The offloaded tool result message (empty contents) must be skipped during seeding.
        // Expect: system + user("Run a tool") + assistant(tool-call) + user("What happened?") = 4.
        Assert.Equal(4, sent.Count);
        Assert.Equal(ChatRole.System,    sent[0].Role);
        Assert.Equal(ChatRole.User,      sent[1].Role);
        Assert.Equal(ChatRole.Assistant, sent[2].Role);
        Assert.Equal(ChatRole.User,      sent[3].Role);

        // Verify the tool-call assistant message was reconstructed correctly.
        FunctionCallContent fc = Assert.IsType<FunctionCallContent>(sent[2].Contents[0]);
        Assert.Equal("run_thing", fc.Name);
        Assert.Equal(callId, fc.CallId);
    }

    /// <summary>
    /// After a turn completes, PersistNewHistoryEntriesAsync splices the store's returned record form
    /// back into _history. If the store truncates or transforms the content (e.g. attachment offloading),
    /// the second turn must send the store's version, not the original in-memory content.
    /// This test fails if the reconciliation foreach in PersistNewHistoryEntriesAsync is removed.
    /// </summary>
    [Fact]
    public async Task AfterPersist_D6Splice_SecondTurnSendsStoreForm()
    {
        string sessionId = "session-d6-splice";
        const string fullText = "FULL ASSISTANT RESPONSE";
        const string truncatedText = "truncated";

        // An offloading store that replaces the assistant response text with a truncated sentinel.
        // This simulates what a real attachment-offloading store does: the MessageRecord it writes
        // differs from the ChatMessage that was passed in.
        OffloadingSessionStore store = new(sessionId, replacementAssistantText: truncatedText);
        ActiveSessionHandler sessionHandler = new(sessionId);
        CapturingChatClient client = new(fullText);
        (TurnHandler handler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(client),
            sessionHandler: sessionHandler,
            sessionStore: store);

        // Turn 1: handler adds user + assistant(fullText) to _history, then persists.
        // OffloadingSessionStore returns records where assistant text = truncatedText.
        // The D6 splice must replace _history[assistantIdx] with the truncated form.
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r-d6-splice-1", Message = "Hello" }, CancellationToken.None);

        // Turn 2: submit another user message on the SAME handler instance.
        // The messages sent to the provider must include the truncated assistant text, not fullText.
        // client.LastMessages is updated on each call; after turn 2 it reflects turn-2's input.
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r-d6-splice-2", Message = "Follow up" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = client.LastMessages;

        // History on turn 2: system + user("Hello") + assistant(truncatedText) + user("Follow up").
        // The assistant entry must carry the truncated form from the store, not the original fullText.
        ChatMessage? assistantMsg = sent.FirstOrDefault(m => m.Role == ChatRole.Assistant);
        Assert.NotNull(assistantMsg);
        string? assistantText = assistantMsg.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
        Assert.Equal(truncatedText, assistantText);
        Assert.DoesNotContain(sent, m =>
            m.Role == ChatRole.Assistant &&
            m.Contents.OfType<TextContent>().Any(t => t.Text == fullText));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MessageRecord MakeMessage(string role, string text) =>
        new()
        {
            EntryId   = NewId(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = role,
            Parts     = [new TextPart { Text = text }]
        };

    private static string NewId() => Guid.NewGuid().ToString();
}

/// <summary>
/// IChatClient that captures the messages list on each streaming call.
/// </summary>
internal sealed class CapturingChatClient : IChatClient
{
    private readonly string _text;
    private IReadOnlyList<ChatMessage> _last = [];

    public CapturingChatClient(string text) => _text = text;

    public IReadOnlyList<ChatMessage> LastMessages => _last;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _last = messages.ToList();
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _text)]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _last = messages.ToList();
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
    }

    public void Dispose() { }
}

/// <summary>
/// ISessionStore that returns a pre-seeded set of records for ReadRecordsAsync.
/// AppendMessagesAsync is a no-op (returns empty record list).
/// </summary>
internal sealed class SeededSessionStore : ISessionStore
{
    private readonly string _sessionId;
    private readonly IReadOnlyList<SessionLogLine> _records;

    public SeededSessionStore(string sessionId, IReadOnlyList<SessionLogLine> records)
    {
        _sessionId = sessionId;
        _records = records;
    }

    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionLogLine> result = sessionId == _sessionId ? _records : [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        MessageRecord[] records = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m =>
            {
                (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                return new MessageRecord { EntryId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Role = role, Parts = parts };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<MessageRecord>>(records);
    }

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public string GetSessionDirectory(string sessionId) => throw new NotSupportedException();
    public Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// ISessionStore that records all AppendMessagesAsync calls for assertion.
/// Returns identity-mapped (non-offloading) records.
/// </summary>
internal sealed class RecordingSessionStore : ISessionStore
{
    private readonly string _sessionId;
    private readonly IReadOnlyList<SessionLogLine> _seeded;
    private readonly List<ChatMessage> _appended = [];

    public RecordingSessionStore(string sessionId, IReadOnlyList<SessionLogLine> seeded)
    {
        _sessionId = sessionId;
        _seeded = seeded;
    }

    public IReadOnlyList<ChatMessage> AppendedMessages => _appended;

    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionLogLine>>(sessionId == _sessionId ? _seeded : []);

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        MessageRecord[] records = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m =>
            {
                _appended.Add(m);
                (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                return new MessageRecord { EntryId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Role = role, Parts = parts };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<MessageRecord>>(records);
    }

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public string GetSessionDirectory(string sessionId) => throw new NotSupportedException();
    public Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// ISessionStore that simulates attachment offloading: AppendMessagesAsync returns records where
/// assistant text content is replaced with a caller-specified truncated sentinel. This lets tests
/// verify that PersistNewHistoryEntriesAsync splices the store form back into _history.
/// </summary>
internal sealed class OffloadingSessionStore : ISessionStore
{
    private readonly string _sessionId;
    private readonly string _replacementAssistantText;

    public OffloadingSessionStore(string sessionId, string replacementAssistantText)
    {
        _sessionId = sessionId;
        _replacementAssistantText = replacementAssistantText;
    }

    public Task<IReadOnlyList<SessionLogLine>> ReadRecordsAsync(
        string sessionId,
        bool applyCompaction = true,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionLogLine>>([]);

    public Task<IReadOnlyList<MessageRecord>> AppendMessagesAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        MessageRecord[] records = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m =>
            {
                (string role, IReadOnlyList<Part> parts) = ConversationMapper.ToParts(m);
                // Simulate offloading: replace assistant text parts with the truncated sentinel.
                if (m.Role == ChatRole.Assistant)
                    parts = [new TextPart { Text = _replacementAssistantText }];
                return new MessageRecord { EntryId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Role = role, Parts = parts };
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<MessageRecord>>(records);
    }

    public Task<SessionMeta> CreateAsync(string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SessionMeta>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateMetaAsync(SessionMeta meta, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public string GetSessionDirectory(string sessionId) => throw new NotSupportedException();
    public Task<SessionMeta> ForkAsync(string sourceSessionId, string entryId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionMeta> CloneAsync(string sourceSessionId, string? name = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<object>> ReadMessagesAsync(string sessionId, bool applyCompaction = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> AppendMessageAsync(string sessionId, string role, IReadOnlyList<Part> parts, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
