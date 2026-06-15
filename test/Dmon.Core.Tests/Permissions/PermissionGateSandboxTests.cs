using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Permissions;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Permissions;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Permissions;

/// <summary>
/// Gate-level sandbox behaviour tests (task 7.4 — replaces profile-based assertions).
///
/// These tests operate at the <see cref="PermissionGateChatClient"/> level and verify:
/// - writes inside <c>assets/&lt;session_id&gt;/</c> are allowed without prompting when
///   <see cref="AssetsOptions"/> is registered and mode is <see cref="PermissionMode.Sandbox"/>;
/// - writes outside the asset dir fall through to the normal prompt flow;
/// - a configured Write.Deny entry inside the asset dir is still denied (denylist wins);
/// - a path escape attempt (via <c>..</c>) is not allowed without prompting;
/// - under <c>coding</c> mode (no <see cref="PermissionModeOptions"/> or Coding mode) the
///   same write always prompts (no sandbox allowance).
///
/// The production code derives the asset directory root from <see cref="AssetsOptions.Path"/>
/// (or <c>Directory.GetCurrentDirectory()</c> as fallback). Tests pass the workspace explicitly
/// via <c>AssetsOptions</c> so no CWD mutation is required.
///
/// Unit-level symlink/escape coverage is in <see cref="SandboxContainmentCheckerTests"/>.
/// One gate-level escape assertion here proves the gate wires the checker in.
/// </summary>
[Collection("FileSystemCwd")]
public sealed class PermissionGateSandboxTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _sessionId;
    private readonly string _assetDir;

    public PermissionGateSandboxTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_workspace);
        _sessionId = "sess-sandbox";
        _assetDir = Path.GetFullPath(Path.Combine(_workspace, "assets", _sessionId));
        Directory.CreateDirectory(_assetDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    // ── test doubles ─────────────────────────────────────────────────────────

    private sealed class StubInnerClient(List<ChatMessage> response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
            => Task.FromResult(new ChatResponse(response));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (ChatMessage msg in response)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Contents);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        public void Register(string extensionName, IToolExtension extension, IEnumerable<AIFunction> tools) { }
        public IToolExtension? FindExtension(string toolName) => null;
        public void Unregister(string extensionName) { }
        public IReadOnlyList<AIFunction> GetAll() => [];
        public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => [];
        public void Clear() { }
    }

    private sealed class StubSessionHandler(string sessionId) : ISessionHandler
    {
        private readonly SessionMeta _session = new() { Id = sessionId };

        public SessionMeta? CurrentSession => _session;
        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubPermissionSettings(PermissionSettings settings) : IPermissionSettings
    {
        public PermissionSettings Settings => settings;
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubPolicy(PermissionSettings? projectSettings = null) : IPermissionPolicy
    {
        public IPermissionSettings ProjectSettings { get; } =
            new StubPermissionSettings(projectSettings ?? new PermissionSettings());

        public IPermissionSettings? GlobalSettings => null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FunctionCallContent MakeWriteCall(string callId, string path)
    {
        Dictionary<string, object?> args = new() { ["path"] = path };
        return new FunctionCallContent(callId, "write_file", args);
    }

    /// <summary>
    /// Builds a <see cref="PermissionGateChatClient"/> in sandbox mode with assets rooted at
    /// <paramref name="workspacePath"/>. The gate uses <see cref="AssetsOptions"/> +
    /// <see cref="PermissionModeOptions"/> to enter the sandbox allowance path.
    /// </summary>
    private PermissionGateChatClient BuildSandboxGate(
        List<ChatMessage> innerResponse,
        Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> callback,
        string workspacePath,
        IPermissionPolicy? policy = null)
    {
        return new PermissionGateChatClient(
            new StubInnerClient(innerResponse),
            policy ?? new StubPolicy(),
            new StubToolRegistry(),
            callback,
            new StubSessionHandler(_sessionId),
            assetsOptions: new AssetsOptions(workspacePath),
            permissionModeOptions: new PermissionModeOptions(PermissionMode.Sandbox));
    }

    /// <summary>
    /// Builds a gate in coding mode (no sandbox allowance): no <see cref="PermissionModeOptions"/>
    /// registered so the gate defaults to <see cref="PermissionMode.Coding"/>.
    /// </summary>
    private PermissionGateChatClient BuildCodingGate(
        List<ChatMessage> innerResponse,
        Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> callback)
    {
        return new PermissionGateChatClient(
            new StubInnerClient(innerResponse),
            new StubPolicy(),
            new StubToolRegistry(),
            callback,
            new StubSessionHandler(_sessionId));
        // assetsOptions and permissionModeOptions intentionally absent → coding mode, no sandbox
    }

    // ── 7.4 — write inside asset dir is allowed without prompting ────────────

    [Fact]
    public async Task GetResponseAsync_Sandbox_WriteInsideAssetDir_AllowedWithoutPrompt()
    {
        string targetInsideAsset = Path.Combine(_assetDir, "output.txt");
        FunctionCallContent call = MakeWriteCall("call-1", targetInsideAsset);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
        {
            callbackInvoked = true;
            return Task.FromResult(true);
        }, _workspace);

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        // Callback must NOT be invoked — the sandbox allowance short-circuits to Allow.
        Assert.False(callbackInvoked, "Confirm callback must not be invoked for a path inside the asset dir.");

        // The tool call must be in the allowed (assistant) message.
        ChatMessage assistantMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
        Assert.Contains(call, assistantMsg.Contents);
    }

    // ── 7.4 — write outside asset dir prompts normally ───────────────────────

    [Fact]
    public async Task GetResponseAsync_Sandbox_WriteOutsideAssetDir_Prompts()
    {
        // A path outside the asset dir — under the workspace root but not in assets/<session>.
        string targetOutside = Path.GetFullPath(Path.Combine(_workspace, "src", "main.cs"));
        FunctionCallContent call = MakeWriteCall("call-2", targetOutside);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
        {
            callbackInvoked = true;
            return Task.FromResult(true); // approve so the call can proceed
        }, _workspace);

        await gate.GetResponseAsync([], null, CancellationToken.None);

        // The confirm callback MUST be invoked: outside the asset dir = standard prompt flow.
        Assert.True(callbackInvoked, "Confirm callback must be invoked for a path outside the asset dir.");
    }

    // ── 7.4 — denylist match inside asset dir is still denied ────────────────

    [Fact]
    public async Task GetResponseAsync_Sandbox_DenylistMatchInsideAssetDir_IsDenied()
    {
        string targetInsideAsset = Path.Combine(_assetDir, "secret.txt");

        // Configure a Write.Deny entry matching the path inside the asset dir.
        PermissionSettings settings = new()
        {
            Write = new TierSettings { Deny = [targetInsideAsset] }
        };
        StubPolicy policy = new(settings);

        FunctionCallContent call = MakeWriteCall("call-3", targetInsideAsset);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = new(
            new StubInnerClient(inner),
            policy,
            new StubToolRegistry(),
            (_, _) => { callbackInvoked = true; return Task.FromResult(true); },
            new StubSessionHandler(_sessionId),
            assetsOptions: new AssetsOptions(_workspace),
            permissionModeOptions: new PermissionModeOptions(PermissionMode.Sandbox));

        ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

        // Denylist wins — no prompt, result is a denied error message.
        Assert.False(callbackInvoked, "Confirm callback must not be invoked when denylist denies.");

        // The response should contain a tool-role denied message (not an assistant message).
        ChatMessage toolMsg = Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
    }

    // ── 7.4 — path escape via .. is not silently allowed ─────────────────────
    // Unit-level escape matrix is in SandboxContainmentCheckerTests; this asserts
    // the gate wires the checker correctly (one case suffices at gate level).

    [Fact]
    public async Task GetResponseAsync_Sandbox_DotDotEscapeOutsideAssetDir_Prompts()
    {
        // Construct a path that traverses .. out of the asset dir, landing in the workspace root.
        string escapePath = Path.Combine(_assetDir, "..", "..", "escape.txt");
        FunctionCallContent call = MakeWriteCall("call-4", escapePath);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
        {
            callbackInvoked = true;
            return Task.FromResult(true);
        }, _workspace);

        await gate.GetResponseAsync([], null, CancellationToken.None);

        // The escape path must NOT be silently allowed — it must prompt.
        Assert.True(callbackInvoked, "Escape path via .. must not be silently allowed; callback must be invoked.");
    }

    // ── 7.4 — coding mode control: same write always prompts ─────────────────

    [Fact]
    public async Task GetResponseAsync_Coding_WriteInsideAssetDir_StillPrompts()
    {
        // Coding mode: no sandbox allowance regardless of the path.
        string targetInsideAsset = Path.Combine(_assetDir, "output.txt");
        FunctionCallContent call = MakeWriteCall("call-5", targetInsideAsset);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = BuildCodingGate(inner, (_, _) =>
        {
            callbackInvoked = true;
            return Task.FromResult(true);
        });

        await gate.GetResponseAsync([], null, CancellationToken.None);

        // Coding mode must always prompt — the sandbox allowance must not apply.
        Assert.True(callbackInvoked, "Coding mode must always prompt; no sandbox allowance.");
    }

    // ── 7.4 — UseAssets absent: no sandbox allowance even in sandbox mode ────

    [Fact]
    public async Task GetResponseAsync_Sandbox_NoAssetsOptions_StillPrompts()
    {
        // PermissionModeOptions is Sandbox but AssetsOptions is absent → no asset dir →
        // guard in ApplySandboxAllowance falls through to the normal prompt path.
        string targetInsideAsset = Path.Combine(_assetDir, "output.txt");
        FunctionCallContent call = MakeWriteCall("call-6", targetInsideAsset);
        List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

        bool callbackInvoked = false;
        PermissionGateChatClient gate = new(
            new StubInnerClient(inner),
            new StubPolicy(),
            new StubToolRegistry(),
            (_, _) => { callbackInvoked = true; return Task.FromResult(true); },
            new StubSessionHandler(_sessionId),
            assetsOptions: null,
            permissionModeOptions: new PermissionModeOptions(PermissionMode.Sandbox));

        await gate.GetResponseAsync([], null, CancellationToken.None);

        Assert.True(callbackInvoked, "Without AssetsOptions no sandbox allowance should apply.");
    }
}
