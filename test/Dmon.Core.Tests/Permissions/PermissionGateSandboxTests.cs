using Dmon.Abstractions.Profiles;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Extensions;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Sessions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Permissions;

/// <summary>
/// Gate-level sandbox behaviour tests for Group 7.3.
///
/// These tests operate at the <see cref="PermissionGateChatClient"/> level and verify:
/// - writes inside <c>assets/&lt;session_id&gt;/</c> are allowed without prompting;
/// - writes outside the asset dir fall through to the normal prompt flow;
/// - a configured Write.Deny entry inside the asset dir is still denied (denylist wins);
/// - a path escape attempt (via <c>..</c>) is not allowed without prompting;
/// - under <c>coding</c> mode the same write always prompts (no sandbox allowance).
///
/// Unit-level symlink/escape coverage is in <see cref="SandboxContainmentCheckerTests"/>.
/// One gate-level escape assertion here proves the gate wires the checker in.
///
/// The production code calls <see cref="System.IO.Directory.GetCurrentDirectory()"/> to
/// derive the asset directory root. Tests therefore set CWD to the temp workspace for the
/// duration of each sandbox scenario. The class is in <c>FileSystemCwd</c> collection
/// (DisableParallelization=true) so CWD mutations do not race with other tests.
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
        public void Register(string extensionName, IDmonExtension extension, IEnumerable<AIFunction> tools) { }
        public IDmonExtension? FindExtension(string toolName) => null;
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

    private sealed class StubNoSessionHandler : ISessionHandler
    {
        public SessionMeta? CurrentSession => null;
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

    private sealed class StubResolver(AgentProfile profile) : IAgentProfileResolver
    {
        public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken)
            => Task.FromResult(profile);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static FunctionCallContent MakeWriteCall(string callId, string path)
    {
        Dictionary<string, object?> args = new() { ["path"] = path };
        return new FunctionCallContent(callId, "write_file", args);
    }

    private static async Task<AgentProfileContext> ResolvedContextAsync(AgentProfile profile)
    {
        AgentProfileContext ctx = new();
        await ctx.EnsureResolvedAsync(new StubResolver(profile), null, CancellationToken.None);
        return ctx;
    }

    private PermissionGateChatClient BuildSandboxGate(
        List<ChatMessage> innerResponse,
        Func<ToolConfirmRequestEvent, CancellationToken, Task<bool>> callback,
        AgentProfileContext profileContext,
        IPermissionPolicy? policy = null)
    {
        return new PermissionGateChatClient(
            new StubInnerClient(innerResponse),
            policy ?? new StubPolicy(),
            new StubToolRegistry(),
            callback,
            profileContext,
            new StubSessionHandler(_sessionId));
    }

    // ── 7.3 — write inside asset dir is allowed without prompting ────────────
    // NOTE: The production code derives the asset directory root from
    // Directory.GetCurrentDirectory(). Tests in this class set CWD to _workspace
    // and compute target paths AFTER that, so the gate's asset root and the test's
    // target path agree. CWD mutations are process-global; these tests are marked
    // [Collection("FileSystemCwd")] to prevent parallel interference.

    [Fact]
    public async Task GetResponseAsync_Sandbox_WriteInsideAssetDir_AllowedWithoutPrompt()
    {
        AgentProfile sandboxProfile = new("sandbox", "You are a sandbox agent.", Assets: true, PermissionMode.Sandbox);
        AgentProfileContext ctx = await ResolvedContextAsync(sandboxProfile);

        string prevDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workspace);

            // Compute the target AFTER setting CWD so both the test and the gate agree on the root.
            string assetDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", _sessionId));
            Directory.CreateDirectory(assetDir);
            string targetInsideAsset = Path.Combine(assetDir, "output.txt");

            FunctionCallContent call = MakeWriteCall("call-1", targetInsideAsset);
            List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

            bool callbackInvoked = false;
            PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult(true);
            }, ctx);

            ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

            // Callback must NOT be invoked — the sandbox allowance short-circuits to Allow.
            Assert.False(callbackInvoked, "Confirm callback must not be invoked for a path inside the asset dir.");

            // The tool call must be in the allowed (assistant) message.
            ChatMessage assistantMsg = Assert.Single(response.Messages);
            Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
            Assert.Contains(call, assistantMsg.Contents);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevDir);
        }
    }

    // ── 7.3 — write outside asset dir prompts normally ───────────────────────

    [Fact]
    public async Task GetResponseAsync_Sandbox_WriteOutsideAssetDir_Prompts()
    {
        AgentProfile sandboxProfile = new("sandbox", "You are a sandbox agent.", Assets: true, PermissionMode.Sandbox);
        AgentProfileContext ctx = await ResolvedContextAsync(sandboxProfile);

        string prevDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workspace);

            // A path outside the asset dir — under the workspace root but not in assets/<session>.
            string targetOutside = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "main.cs"));
            FunctionCallContent call = MakeWriteCall("call-2", targetOutside);
            List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

            bool callbackInvoked = false;
            PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult(true); // approve so the call can proceed
            }, ctx);

            await gate.GetResponseAsync([], null, CancellationToken.None);

            // The confirm callback MUST be invoked: outside the asset dir = standard prompt flow.
            Assert.True(callbackInvoked, "Confirm callback must be invoked for a path outside the asset dir.");
        }
        finally
        {
            Directory.SetCurrentDirectory(prevDir);
        }
    }

    // ── 7.3 — denylist match inside asset dir is still denied ────────────────

    [Fact]
    public async Task GetResponseAsync_Sandbox_DenylistMatchInsideAssetDir_IsDenied()
    {
        AgentProfile sandboxProfile = new("sandbox", "You are a sandbox agent.", Assets: true, PermissionMode.Sandbox);
        AgentProfileContext ctx = await ResolvedContextAsync(sandboxProfile);

        string prevDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workspace);

            string assetDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", _sessionId));
            Directory.CreateDirectory(assetDir);
            string targetInsideAsset = Path.Combine(assetDir, "secret.txt");

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
                ctx,
                new StubSessionHandler(_sessionId));

            ChatResponse response = await gate.GetResponseAsync([], null, CancellationToken.None);

            // Denylist wins — no prompt, result is a denied error message.
            Assert.False(callbackInvoked, "Confirm callback must not be invoked when denylist denies.");

            // The response should contain a tool-role denied message (not an assistant message).
            ChatMessage toolMsg = Assert.Single(response.Messages);
            Assert.Equal(ChatRole.Tool, toolMsg.Role);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevDir);
        }
    }

    // ── 7.3 — path escape via .. is not silently allowed ─────────────────────
    // Unit-level escape matrix is in SandboxContainmentCheckerTests; this asserts
    // the gate wires the checker correctly (one case suffices at gate level).

    [Fact]
    public async Task GetResponseAsync_Sandbox_DotDotEscapeOutsideAssetDir_Prompts()
    {
        AgentProfile sandboxProfile = new("sandbox", "You are a sandbox agent.", Assets: true, PermissionMode.Sandbox);
        AgentProfileContext ctx = await ResolvedContextAsync(sandboxProfile);

        string prevDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workspace);

            string assetDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", _sessionId));
            Directory.CreateDirectory(assetDir);

            // Construct a path that traverses .. out of the asset dir, landing in the workspace root.
            string escapePath = Path.Combine(assetDir, "..", "..", "escape.txt");
            FunctionCallContent call = MakeWriteCall("call-4", escapePath);
            List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

            bool callbackInvoked = false;
            PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult(true);
            }, ctx);

            await gate.GetResponseAsync([], null, CancellationToken.None);

            // The escape path must NOT be silently allowed — it must prompt.
            Assert.True(callbackInvoked, "Escape path via .. must not be silently allowed; callback must be invoked.");
        }
        finally
        {
            Directory.SetCurrentDirectory(prevDir);
        }
    }

    // ── 7.3 — coding mode control: same write always prompts ─────────────────

    [Fact]
    public async Task GetResponseAsync_Coding_WriteInsideAssetDir_StillPrompts()
    {
        // Coding mode: no sandbox allowance regardless of the path.
        AgentProfile codingProfile = new("coding", "Coding persona.", Assets: false, PermissionMode.Coding);
        AgentProfileContext ctx = await ResolvedContextAsync(codingProfile);

        string prevDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_workspace);

            string assetDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", _sessionId));
            Directory.CreateDirectory(assetDir);
            string targetInsideAsset = Path.Combine(assetDir, "output.txt");

            FunctionCallContent call = MakeWriteCall("call-5", targetInsideAsset);
            List<ChatMessage> inner = [new ChatMessage(ChatRole.Assistant, [call])];

            bool callbackInvoked = false;
            PermissionGateChatClient gate = BuildSandboxGate(inner, (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult(true);
            }, ctx);

            await gate.GetResponseAsync([], null, CancellationToken.None);

            // Coding mode must always prompt — the sandbox allowance must not apply.
            Assert.True(callbackInvoked, "Coding mode must always prompt; no sandbox allowance.");
        }
        finally
        {
            Directory.SetCurrentDirectory(prevDir);
        }
    }
}
