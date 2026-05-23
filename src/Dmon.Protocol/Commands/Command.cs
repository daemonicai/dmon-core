using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

/// <summary>
/// Base type for all host-to-core commands.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionCreateCommand), typeDiscriminator: "session.create")]
[JsonDerivedType(typeof(SessionForkCommand), typeDiscriminator: "session.fork")]
[JsonDerivedType(typeof(SessionCloneCommand), typeDiscriminator: "session.clone")]
[JsonDerivedType(typeof(SessionLoadCommand), typeDiscriminator: "session.load")]
[JsonDerivedType(typeof(SessionListCommand), typeDiscriminator: "session.list")]
[JsonDerivedType(typeof(SessionSetNameCommand), typeDiscriminator: "session.setName")]
[JsonDerivedType(typeof(SessionGetStatsCommand), typeDiscriminator: "session.getStats")]
[JsonDerivedType(typeof(SessionGetMessagesCommand), typeDiscriminator: "session.getMessages")]
[JsonDerivedType(typeof(SessionCompactCommand), typeDiscriminator: "session.compact")]
[JsonDerivedType(typeof(TurnSubmitCommand), typeDiscriminator: "turn.submit")]
[JsonDerivedType(typeof(TurnSteerCommand), typeDiscriminator: "turn.steer")]
[JsonDerivedType(typeof(TurnFollowUpCommand), typeDiscriminator: "turn.followUp")]
[JsonDerivedType(typeof(TurnAbortCommand), typeDiscriminator: "turn.abort")]
[JsonDerivedType(typeof(ToolConfirmResponseCommand), typeDiscriminator: "tool.confirmResponse")]
[JsonDerivedType(typeof(UiInputResponseCommand), typeDiscriminator: "ui.inputResponse")]
[JsonDerivedType(typeof(ModelSetCommand), typeDiscriminator: "model.set")]
[JsonDerivedType(typeof(ModelCycleCommand), typeDiscriminator: "model.cycle")]
[JsonDerivedType(typeof(ModelListCommand), typeDiscriminator: "model.list")]
[JsonDerivedType(typeof(ExtensionLoadCommand), typeDiscriminator: "extension.load")]
[JsonDerivedType(typeof(ExtensionUnloadCommand), typeDiscriminator: "extension.unload")]
[JsonDerivedType(typeof(ExtensionPromoteCommand), typeDiscriminator: "extension.promote")]
[JsonDerivedType(typeof(ThinkingSetCommand), typeDiscriminator: "thinking.set")]
[JsonDerivedType(typeof(ThinkingCycleCommand), typeDiscriminator: "thinking.cycle")]
[JsonDerivedType(typeof(AuthLoginCommand), typeDiscriminator: "auth.login")]
[JsonDerivedType(typeof(AuthLogoutCommand), typeDiscriminator: "auth.logout")]
[JsonDerivedType(typeof(AuthStatusCommand), typeDiscriminator: "auth.status")]
public abstract record Command
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}