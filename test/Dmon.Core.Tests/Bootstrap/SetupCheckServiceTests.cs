using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Core.Bootstrap;
using Dmon.Core.Tests.Fakes;
using Dmon.Protocol.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Core.Tests.Bootstrap;

public sealed class SetupCheckServiceTests
{
    // ─── helpers ──────────────────────────────────────────────

    private static FakeProviderFactory MakeFactory(string adapterName, string defaultEnvVar) =>
        new(adapterName, $"{adapterName}-model", defaultEnvVar);

    private static SetupCheckService Build(
        IEnumerable<ProviderConfig> configs,
        IEnumerable<IProviderFactory> factories,
        FakeEventEmitter emitter) =>
        new(configs, factories, emitter, NullLogger<SetupCheckService>.Instance);

    // ─── tests ────────────────────────────────────────────────

    [Fact]
    public async Task NoProviders_EmitsSetupRequiredEvent()
    {
        FakeProviderFactory factoryA = MakeFactory("fake-a", "TEST_FAKE_KEY_A");
        FakeProviderFactory factoryB = MakeFactory("fake-b", "TEST_FAKE_KEY_B");
        FakeEventEmitter emitter = new();

        SetupCheckService svc = Build([], [factoryA, factoryB], emitter);
        await svc.RunAsync(CancellationToken.None);

        SetupRequiredEvent evt = Assert.Single(emitter.Emitted.OfType<SetupRequiredEvent>());
        Assert.Equal(2, evt.Adapters.Count);
        Assert.All(evt.Adapters, a => Assert.False(a.EnvVarDetected));
    }

    [Fact]
    public async Task NoProviders_OneEnvVarSet_DetectsCorrectAdapter()
    {
        FakeProviderFactory factoryA = MakeFactory("fake-a", "TEST_FAKE_KEY_A");
        FakeProviderFactory factoryB = MakeFactory("fake-b", "TEST_FAKE_KEY_B");
        FakeEventEmitter emitter = new();

        Environment.SetEnvironmentVariable("TEST_FAKE_KEY_A", "dummy-value");
        try
        {
            SetupCheckService svc = Build([], [factoryA, factoryB], emitter);
            await svc.RunAsync(CancellationToken.None);

            SetupRequiredEvent evt = Assert.Single(emitter.Emitted.OfType<SetupRequiredEvent>());
            AdapterInfo infoA = Assert.Single(evt.Adapters, a => a.Name == "fake-a");
            AdapterInfo infoB = Assert.Single(evt.Adapters, a => a.Name == "fake-b");
            Assert.True(infoA.EnvVarDetected);
            Assert.False(infoB.EnvVarDetected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_FAKE_KEY_A", null);
        }
    }

    [Fact]
    public async Task NoProviders_MultipleEnvVarsSet_DetectsMultiple()
    {
        FakeProviderFactory factoryA = MakeFactory("fake-a", "TEST_FAKE_KEY_A");
        FakeProviderFactory factoryB = MakeFactory("fake-b", "TEST_FAKE_KEY_B");
        FakeProviderFactory factoryC = MakeFactory("fake-c", "TEST_FAKE_KEY_C");
        FakeEventEmitter emitter = new();

        Environment.SetEnvironmentVariable("TEST_FAKE_KEY_A", "val-a");
        Environment.SetEnvironmentVariable("TEST_FAKE_KEY_B", "val-b");
        try
        {
            SetupCheckService svc = Build([], [factoryA, factoryB, factoryC], emitter);
            await svc.RunAsync(CancellationToken.None);

            SetupRequiredEvent evt = Assert.Single(emitter.Emitted.OfType<SetupRequiredEvent>());
            Assert.True(Assert.Single(evt.Adapters, a => a.Name == "fake-a").EnvVarDetected);
            Assert.True(Assert.Single(evt.Adapters, a => a.Name == "fake-b").EnvVarDetected);
            Assert.False(Assert.Single(evt.Adapters, a => a.Name == "fake-c").EnvVarDetected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_FAKE_KEY_A", null);
            Environment.SetEnvironmentVariable("TEST_FAKE_KEY_B", null);
        }
    }

    [Fact]
    public async Task HasProviders_DoesNotEmitEvent()
    {
        FakeProviderFactory factory = MakeFactory("fake-a", "TEST_FAKE_KEY_A");
        FakeEventEmitter emitter = new();

        ProviderConfig config = new()
        {
            Name = "test",
            Adapter = "fake-a",
            Auth = new ProviderAuthConfig { Type = "envVar", EnvVar = "TEST_FAKE_KEY_A" }
        };

        SetupCheckService svc = Build([config], [factory], emitter);
        await svc.RunAsync(CancellationToken.None);

        Assert.Empty(emitter.Emitted);
    }

    // ─── fakes ────────────────────────────────────────────────

    private sealed class FakeProviderFactory(string adapterName, string defaultModelId, string defaultEnvVar)
        : IProviderFactory
    {
        public string AdapterName => adapterName;
        public string DisplayName => adapterName;
        public string DefaultModelId => defaultModelId;
        public string DefaultEnvVar => defaultEnvVar;

        public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not needed in setup-check tests.");

        public ChatClientCapabilities GetCapabilities(string modelId) => new();

        public ValueTask<IChatClient> CreateAsync(
            ProviderConfig config,
            string? apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed in setup-check tests.");
    }
}
