using Dmon.Abstractions.Profiles;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
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
/// Verifies that TurnHandler passes the session's persisted profile name to the
/// IAgentProfileResolver (task 3.2): a session with profile="X" → requestedProfile="X";
/// a session with no profile → requestedProfile=null.
/// </summary>
public sealed class TurnHandlerProfileTests
{
    [Fact]
    public async Task Submit_SessionWithProfile_PassesProfileToResolver()
    {
        CapturingProfileResolver resolver = new();
        ProfiledSessionHandler sessionHandler = new("test-session", profile: "researcher");
        (TurnHandler handler, _) = CreateHandler(resolver, sessionHandler);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "hello" }, CancellationToken.None);

        Assert.Equal("researcher", resolver.CapturedRequestedProfile);
        Assert.True(resolver.WasInvoked);
    }

    [Fact]
    public async Task Submit_SessionWithNullProfile_PassesNullToResolver()
    {
        CapturingProfileResolver resolver = new();
        ProfiledSessionHandler sessionHandler = new("test-session", profile: null);
        (TurnHandler handler, _) = CreateHandler(resolver, sessionHandler);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "hello" }, CancellationToken.None);

        Assert.True(resolver.WasInvoked);
        Assert.Null(resolver.CapturedRequestedProfile);
    }

    [Fact]
    public async Task Submit_SecondTurn_DoesNotInvokeResolverAgain()
    {
        // The resolver is invoked only once per session (behind _systemPromptInjected guard).
        CapturingProfileResolver resolver = new();
        ProfiledSessionHandler sessionHandler = new("test-session", profile: "researcher");
        (TurnHandler handler, _) = CreateHandler(resolver, sessionHandler);

        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r1", Message = "first" }, CancellationToken.None);
        await handler.SubmitAsync(new TurnSubmitCommand { Id = "r2", Message = "second" }, CancellationToken.None);

        Assert.Equal(1, resolver.InvocationCount);
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    private static (TurnHandler handler, TestEventEmitter emitter) CreateHandler(
        IAgentProfileResolver profileResolver,
        ISessionHandler sessionHandler)
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
            profileResolver,
            new AgentProfileContext(),
            new NoopSessionAssetProvisioner(),
            NullLogger<TurnHandler>.Instance);

        return (handler, emitter);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the requestedProfile argument supplied on the first resolver call.
    /// Returns the built-in coding profile unconditionally so TurnHandler can proceed.
    /// </summary>
    private sealed class CapturingProfileResolver : IAgentProfileResolver
    {
        public bool WasInvoked { get; private set; }
        public int InvocationCount { get; private set; }
        public string? CapturedRequestedProfile { get; private set; }

        public Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            InvocationCount++;
            CapturedRequestedProfile = requestedProfile;
            return Task.FromResult(BuiltInProfiles.Coding);
        }
    }

    /// <summary>
    /// ISessionHandler whose CurrentSession carries the supplied profile name.
    /// </summary>
    private sealed class ProfiledSessionHandler : ISessionHandler
    {
        public ProfiledSessionHandler(string sessionId, string? profile)
        {
            CurrentSession = new SessionMeta
            {
                Id = sessionId,
                Profile = profile,
                Created = DateTimeOffset.UtcNow,
                Modified = DateTimeOffset.UtcNow
            };
        }

        public SessionMeta? CurrentSession { get; }

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
