using Dmon.Abstractions;
using Dmon.Abstractions.Extensions;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Permissions;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Rpc;

/// <summary>
/// Verifies that <see cref="TurnHandler"/> routes <see cref="AssetsOptions"/> and
/// <see cref="PermissionModeOptions"/> DI markers to the asset provisioner correctly
/// (task 7.4 — replaces deleted <c>TurnHandlerProfileTests</c>).
///
/// The provisioner is replaced with a capturing stub so these tests do not touch disk.
/// </summary>
public sealed class TurnHandlerAgentTests
{
    // ── 7.4 — UseAssets active: provisioner is called with assetsEnabled=true ─

    [Fact]
    public async Task Submit_WithAssetsOptions_CallsProvisionerWithAssetsEnabled()
    {
        CapturingProvisioner provisioner = new();
        AssetsOptions assets = new("/workspace");
        (TurnHandler handler, _) = CreateHandler(provisioner, assetsOptions: assets);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "hello" }, CancellationToken.None);

        Assert.True(provisioner.WasInvoked);
        Assert.True(provisioner.LastAssetsEnabled);
        Assert.Equal("/workspace", provisioner.LastWorkspaceRoot);
    }

    // ── 7.4 — UseAssets absent: provisioner called with assetsEnabled=false ───

    [Fact]
    public async Task Submit_NoAssetsOptions_CallsProvisionerWithAssetsDisabled()
    {
        CapturingProvisioner provisioner = new();
        (TurnHandler handler, _) = CreateHandler(provisioner, assetsOptions: null);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "hello" }, CancellationToken.None);

        Assert.True(provisioner.WasInvoked);
        Assert.False(provisioner.LastAssetsEnabled);
    }

    // ── 7.4 — second turn does not re-invoke provisioner ─────────────────────

    [Fact]
    public async Task Submit_SecondTurn_DoesNotInvokeProvisionerAgain()
    {
        CapturingProvisioner provisioner = new();
        (TurnHandler handler, _) = CreateHandler(provisioner, assetsOptions: new AssetsOptions(null));

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "first" }, CancellationToken.None);
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r2", Message = "second" }, CancellationToken.None);

        // Provisioner is called only once (behind _systemPromptInjected guard).
        Assert.Equal(1, provisioner.InvocationCount);
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    private static (TurnHandler handler, TestEventEmitter emitter) CreateHandler(
        ISessionAssetProvisioner provisioner,
        AssetsOptions? assetsOptions = null,
        PermissionModeOptions? permissionModeOptions = null)
    {
        StubChatClient client = new();
        TestEventEmitter emitter = new();
        StubProviderRegistry providers = new(client);
        EmptyToolRegistry tools = new();
        PermitAllPolicy policy = new();
        NoopThinkingHandler thinking = new();
        StubSystemPromptBuilder systemPromptBuilder = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        NoopActiveModelStore store = new();
        MiddlewarePipelineBuilder pipelineBuilder = new(new MiddlewareRegistry(), configuration);
        StubSessionHandler sessionHandler = new("test-session");

        TurnHandler handler = new(
            providers,
            store,
            tools,
            emitter,
            policy,
            thinking,
            sessionHandler,
            systemPromptBuilder,
            pipelineBuilder,
            configuration,
            provisioner,
            NullLogger<TurnHandler>.Instance,
            assetsOptions,
            permissionModeOptions);

        return (handler, emitter);
    }

    // ── Capturing stub ────────────────────────────────────────────────────────

    private sealed class CapturingProvisioner : ISessionAssetProvisioner
    {
        public bool WasInvoked { get; private set; }
        public int InvocationCount { get; private set; }
        public bool LastAssetsEnabled { get; private set; }
        public string? LastWorkspaceRoot { get; private set; }
        public string? LastSessionId { get; private set; }

        public string? Provision(bool assetsEnabled, string? workspaceRoot, string? sessionId)
        {
            WasInvoked = true;
            InvocationCount++;
            LastAssetsEnabled = assetsEnabled;
            LastWorkspaceRoot = workspaceRoot;
            LastSessionId = sessionId;
            return null;
        }
    }

    private sealed class StubSessionHandler(string sessionId) : ISessionHandler
    {
        public SessionMeta? CurrentSession { get; } = new SessionMeta
        {
            Id = sessionId,
            Created = DateTimeOffset.UtcNow,
            Modified = DateTimeOffset.UtcNow
        };

        public Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
