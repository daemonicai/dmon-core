using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// Base type for all core-to-host events.
/// Events are emitted without an <c>id</c> field — they are not request-response.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentReadyEvent), typeDiscriminator: "agentReady")]
[JsonDerivedType(typeof(AgentStartEvent), typeDiscriminator: "agentStart")]
[JsonDerivedType(typeof(AgentEndEvent), typeDiscriminator: "agentEnd")]
[JsonDerivedType(typeof(BootstrapNoticeEvent), typeDiscriminator: "bootstrapNotice")]
[JsonDerivedType(typeof(ProviderSwitchedEvent), typeDiscriminator: "providerSwitched")]
[JsonDerivedType(typeof(CapabilityIgnoredEvent), typeDiscriminator: "capabilityIgnored")]
[JsonDerivedType(typeof(ExtensionErrorEvent), typeDiscriminator: "extensionError")]
[JsonDerivedType(typeof(RetryAttemptEvent), typeDiscriminator: "retryAttempt")]
[JsonDerivedType(typeof(ErrorEvent), typeDiscriminator: "error")]
[JsonDerivedType(typeof(UiInputRequestEvent), typeDiscriminator: "ui.inputRequest")]
[JsonDerivedType(typeof(TurnStartEvent), typeDiscriminator: "turnStart")]
[JsonDerivedType(typeof(TurnEndEvent), typeDiscriminator: "turnEnd")]
[JsonDerivedType(typeof(MessageStartEvent), typeDiscriminator: "messageStart")]
[JsonDerivedType(typeof(MessageDeltaEvent), typeDiscriminator: "messageDelta")]
[JsonDerivedType(typeof(MessageEndEvent), typeDiscriminator: "messageEnd")]
[JsonDerivedType(typeof(ToolExecutionStartEvent), typeDiscriminator: "toolExecutionStart")]
[JsonDerivedType(typeof(ToolExecutionEndEvent), typeDiscriminator: "toolExecutionEnd")]
[JsonDerivedType(typeof(ToolConfirmRequestEvent), typeDiscriminator: "tool.confirmRequest")]
[JsonDerivedType(typeof(SessionUpdatedEvent), typeDiscriminator: "sessionUpdated")]
[JsonDerivedType(typeof(ExtensionLoadedEvent), typeDiscriminator: "extensionLoaded")]
[JsonDerivedType(typeof(ExtensionUnloadedEvent), typeDiscriminator: "extensionUnloaded")]
[JsonDerivedType(typeof(CompactionStartEvent), typeDiscriminator: "compactionStart")]
[JsonDerivedType(typeof(CompactionEndEvent), typeDiscriminator: "compactionEnd")]
[JsonDerivedType(typeof(AuthLoginCompleteEvent), typeDiscriminator: "auth.loginComplete")]
[JsonDerivedType(typeof(AuthLogoutCompleteEvent), typeDiscriminator: "auth.logoutComplete")]
[JsonDerivedType(typeof(AuthLoginFailedEvent), typeDiscriminator: "auth.loginFailed")]
[JsonDerivedType(typeof(AuthStatusResultEvent), typeDiscriminator: "auth.statusResult")]
[JsonDerivedType(typeof(ModelListResultEvent), typeDiscriminator: "model.listResult")]
[JsonDerivedType(typeof(ModelModelsResultEvent), typeDiscriminator: "model.models.result")]
[JsonDerivedType(typeof(ResponseEvent), typeDiscriminator: "response")]
[JsonDerivedType(typeof(SetupRequiredEvent), typeDiscriminator: "setupRequired")]
[JsonDerivedType(typeof(ProviderConfiguredEvent), typeDiscriminator: "providerConfigured")]
[JsonDerivedType(typeof(SystemNoticeEvent), typeDiscriminator: "system.notice")]
[JsonDerivedType(typeof(WizardStepEvent), typeDiscriminator: "wizard.step")]
public abstract record Event
{
}