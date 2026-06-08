using System.Text.Json;
using Dmon.Core.Session;
using Dmon.Protocol.Conversation;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

// ---------------------------------------------------------------------------
// Group 4: tool-call history capture — integration tests
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests that use a real disk-backed <see cref="SessionStore"/> to verify
/// that tool call/result parts are written to <c>messages.jsonl</c> and restored on resume.
/// </summary>
public sealed class TurnHandlerToolCallHistoryPersistenceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TurnHandlerToolCallHistoryPersistenceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private (ISessionStore store, IAttachmentStore attachmentStore) CreateStores(int? thresholdBytes = null)
    {
        DiskSessionResolver resolver = new(_tempRoot);
        IConfigurationBuilder builder = new ConfigurationBuilder();

        if (thresholdBytes.HasValue)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmon:Session:AttachmentThresholdBytes"] = thresholdBytes.Value.ToString()
            });
        }

        IConfiguration config = builder.Build();
        AttachmentStore attachmentStore = new(resolver, config);
        SessionStore store = new(resolver, attachmentStore, NullLogger<SessionStore>.Instance, NullLoggerFactory.Instance, config);
        return (store, attachmentStore);
    }

    // ── 4.1: tool turn records toolCall + toolResult parts in messages.jsonl;
    //         resuming in a fresh core restores them into context ──────────────

    /// <summary>
    /// Runs a tool turn via TurnHandler against a real disk SessionStore, then reads the
    /// raw <c>messages.jsonl</c> and asserts both <c>toolCall</c> and <c>toolResult</c>
    /// discriminated parts were written.
    /// </summary>
    [Fact]
    public async Task ToolTurn_WritesToolCallAndToolResultPartsToMessagesJsonl()
    {
        (ISessionStore store, _) = CreateStores();
        SessionMeta session = await store.CreateAsync();

        AIFunction tool = AIFunctionFactory.Create(
            (string input) => "the-answer",
            "lookup",
            "Stub lookup tool");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("lookup", "Done.");
        ActiveSessionHandler sessionHandler = new(session.Id);

        (Dmon.Core.Rpc.TurnHandler handler, _) = ToolTurnHandlerFactory.Create(
            provider,
            tools,
            sessionHandler,
            store);

        await handler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        // Read raw lines from messages.jsonl — verify part discriminators.
        string messagesPath = Path.Combine(store.GetSessionDirectory(session.Id), "messages.jsonl");
        string[] lines = await File.ReadAllLinesAsync(messagesPath);
        string content = string.Concat(lines);

        Assert.Contains("\"toolCall\"", content);
        Assert.Contains("\"toolResult\"", content);
    }

    /// <summary>
    /// After a tool turn, verifies that ReadRecordsAsync returns a <see cref="MessageRecord"/>
    /// with role "assistant" carrying a <see cref="ToolCallPart"/> and a record with role "tool"
    /// carrying a <see cref="ToolResultPart"/>.
    /// </summary>
    [Fact]
    public async Task ToolTurn_ReadRecordsReturnsToolCallAndToolResultParts()
    {
        (ISessionStore store, _) = CreateStores();
        SessionMeta session = await store.CreateAsync();

        AIFunction tool = AIFunctionFactory.Create(
            (string input) => "lookup-result",
            "lookup",
            "Stub lookup tool");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("lookup", "Done.");
        ActiveSessionHandler sessionHandler = new(session.Id);

        (Dmon.Core.Rpc.TurnHandler handler, _) = ToolTurnHandlerFactory.Create(
            provider,
            tools,
            sessionHandler,
            store);

        await handler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);

        Assert.Contains(records,
            r => r is MessageRecord mr && mr.Role == "assistant" && mr.Parts.OfType<ToolCallPart>().Any());
        Assert.Contains(records,
            r => r is MessageRecord mr && mr.Role == "tool" && mr.Parts.OfType<ToolResultPart>().Any());
    }

    /// <summary>
    /// After a tool turn with a real store, seeds a fresh TurnHandler from the same session.
    /// The second handler's first provider call must include the assistant tool-call message and
    /// the tool-role result message in the context sent to the provider.
    /// </summary>
    [Fact]
    public async Task ToolTurn_FreshCoreResume_RestoresToolCallAndResultIntoContext()
    {
        const string callId = "cid-1";
        (ISessionStore store, _) = CreateStores();
        SessionMeta session = await store.CreateAsync();

        AIFunction tool = AIFunctionFactory.Create(
            (string input) => "resume-result",
            "lookup",
            "Stub lookup tool");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("lookup", "Done.");
        ActiveSessionHandler sessionHandler = new(session.Id);

        (Dmon.Core.Rpc.TurnHandler firstHandler, _) = ToolTurnHandlerFactory.Create(
            provider,
            tools,
            sessionHandler,
            store);

        await firstHandler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r1", Message = "first" }, CancellationToken.None);

        // Simulate a fresh core: new TurnHandler, same store, same session.
        CapturingChatClient resumeClient = new("resumed-response");
        (Dmon.Core.Rpc.TurnHandler freshHandler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(resumeClient),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await freshHandler.SeedHistoryFromSessionAsync(session.Id, CancellationToken.None);
        await freshHandler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r2", Message = "follow-up" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = resumeClient.LastMessages;

        // Context must include an assistant message with a FunctionCallContent.
        Assert.Contains(sent,
            m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any(fc => fc.CallId == callId));

        // Context must include a tool-role message with a FunctionResultContent.
        Assert.Contains(sent,
            m => m.Role == new ChatRole("tool") && m.Contents.OfType<FunctionResultContent>().Any(fr => fr.CallId == callId));
    }

    // ── 4.3: large tool result offloaded to attachments/<safe-callId>;
    //         D6 replay sends preview form on next turn ─────────────────────────

    /// <summary>
    /// When a tool returns a result that exceeds the attachment threshold, TurnHandler writes
    /// the full content to <c>attachments/&lt;callId&gt;.txt</c> and the D6 reconciliation
    /// splice replaces the in-memory entry with the preview form, so the second turn sends the
    /// preview (truncated) rather than the full content to the provider.
    /// </summary>
    [Fact]
    public async Task LargeToolResult_IsOffloadedToAttachment_AndD6SpliceSendsPreviewOnNextTurn()
    {
        // Threshold = 10 bytes so any non-trivial result is offloaded.
        (ISessionStore store, _) = CreateStores(thresholdBytes: 10);
        SessionMeta session = await store.CreateAsync();

        // 300 chars: above the 10-byte attachment threshold AND above the 200-char preview boundary,
        // so the stored preview is shorter than the original value.
        string largeResult = new string('z', 300);

        AIFunction tool = AIFunctionFactory.Create(
            (string input) => largeResult,
            "big_tool",
            "Tool that returns a large result");
        StubToolRegistry tools = new(tool);
        FunctionCallProviderStub provider = new("big_tool", "Done.");
        ActiveSessionHandler sessionHandler = new(session.Id);

        CapturingChatClient captureClient = new("second-response");
        // Wire first turn with FunctionCallProviderStub; subsequent turns use captureClient
        // once ToolTurnHandlerFactory's inner client is replaced. For simplicity, we reuse
        // the same handler for the two turns.
        (Dmon.Core.Rpc.TurnHandler handler, _) = ToolTurnHandlerFactory.Create(
            provider,
            tools,
            sessionHandler,
            store);

        // Turn 1: runs the tool, result offloaded, D6 splice updates _history.
        await handler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r1", Message = "go" }, CancellationToken.None);

        // Attachment file must exist and contain the full large result.
        string sessionDir = store.GetSessionDirectory(session.Id);
        string attachmentPath = Path.Combine(sessionDir, "attachments", "cid-1.txt");
        Assert.True(File.Exists(attachmentPath), $"Attachment file not found at {attachmentPath}");

        string storedContent = await File.ReadAllTextAsync(attachmentPath);
        Assert.Contains(largeResult, storedContent);

        // The ToolResultPart in messages.jsonl must have attachmentRef and truncated=true.
        IReadOnlyList<SessionLogLine> records = await store.ReadRecordsAsync(session.Id);
        MessageRecord? toolRecord = records.OfType<MessageRecord>().FirstOrDefault(r => r.Role == "tool");
        Assert.NotNull(toolRecord);

        ToolResultPart? storedPart = toolRecord.Parts.OfType<ToolResultPart>().FirstOrDefault();
        Assert.NotNull(storedPart);
        Assert.NotNull(storedPart.AttachmentRef);
        Assert.True(storedPart.Truncated);
        // Preview must be present but shorter than the full result.
        Assert.True(storedPart.Result.HasValue);
        string? preview = storedPart.Result!.Value.GetString();
        Assert.NotNull(preview);
        Assert.True(preview!.Length < largeResult.Length, $"Preview ({preview.Length} chars) should be shorter than full result ({largeResult.Length} chars)");
    }

    /// <summary>
    /// Verifies the D6 reconciliation: after a large-result tool turn, the second turn
    /// sends the preview form (not the full in-memory content) to the provider.
    /// </summary>
    [Fact]
    public async Task LargeToolResult_D6Reconciliation_SecondTurnSendsPreviewNotFullContent()
    {
        (ISessionStore store, _) = CreateStores(thresholdBytes: 10);
        SessionMeta session = await store.CreateAsync();

        string largeResult = new string('z', 300); // > 200-char preview boundary

        AIFunction tool = AIFunctionFactory.Create(
            (string input) => largeResult,
            "big_tool",
            "Tool that returns a large result");
        StubToolRegistry tools = new(tool);

        // The provider returns "Done." after the tool turn; we then do a second turn.
        // We need a CapturingChatClient for the second turn. Use a two-phase approach:
        // run turn 1 with ToolTurnHandlerFactory (which internally uses ToolSupportingProviderRegistry),
        // then seed a fresh handler from the same store and observe what it sends.
        FunctionCallProviderStub firstProvider = new("big_tool", "Done.");
        ActiveSessionHandler sessionHandler = new(session.Id);

        (Dmon.Core.Rpc.TurnHandler firstHandler, _) = ToolTurnHandlerFactory.Create(
            firstProvider,
            tools,
            sessionHandler,
            store);

        await firstHandler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r1", Message = "run it" }, CancellationToken.None);

        // Fresh handler: seed from disk, run a second turn.
        CapturingChatClient captureClient = new("follow-up-answer");
        (Dmon.Core.Rpc.TurnHandler freshHandler, _) = TurnHandlerFactory.Create(
            new StubProviderRegistry(captureClient),
            sessionHandler: sessionHandler,
            sessionStore: store);

        await freshHandler.SeedHistoryFromSessionAsync(session.Id, CancellationToken.None);
        await freshHandler.SubmitAsync(new Dmon.Protocol.Commands.TurnSubmitCommand { Id = "r2", Message = "what did it return?" }, CancellationToken.None);

        IReadOnlyList<ChatMessage> sent = captureClient.LastMessages;

        // The full large string must NOT appear — only the 200-char preview form is sent.
        bool fullResultPresent = sent
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Any(fr =>
            {
                string? s = fr.Result?.ToString();
                return s is not null && s.Contains(largeResult);
            });

        Assert.False(fullResultPresent,
            "Full large result content must not appear in context after offloading — only preview form.");

        // Positive assertion: a FunctionResultContent carrying the preview (200 'z' chars + ellipsis) IS sent.
        string expectedPreview = new string('z', 200) + "…";
        bool previewPresent = sent
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Any(fr =>
            {
                string? s = fr.Result?.ToString();
                return s is not null && s.Contains(expectedPreview);
            });

        Assert.True(previewPresent,
            "The 200-char preview must be present in a FunctionResultContent sent to the provider.");
    }

    private sealed class DiskSessionResolver : ISessionDirectoryResolver
    {
        private readonly string _root;

        public DiskSessionResolver(string root) => _root = root;

        public string Resolve(string workingDirectory) => _root;
    }
}
