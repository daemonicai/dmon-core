using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

public interface IProviderSetupHandler
{
    Task ConfigureAsync(ProviderConfigureCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the provider setup wizard. Emits a sequence of <c>wizard.step</c> events
    /// and completes when the user finishes or cancels.
    /// </summary>
    Task StartWizardAsync(WizardStartCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Delivers an answer (or navigation action) for the currently active wizard step.
    /// Answers whose <c>WizardId</c> does not match the active session are silently ignored.
    /// </summary>
    Task AnswerWizardAsync(WizardAnswerCommand command, CancellationToken cancellationToken);
}
