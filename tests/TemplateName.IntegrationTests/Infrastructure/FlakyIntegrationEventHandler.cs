using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Outbox;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// A second consumer of <see cref="EmailVerificationRequestedIntegrationEvent"/> that fails once when <see cref="FlakySwitch.FailNext"/>
/// is set, so the publisher rethrows and the Auth outbox retries the publishing handler. It names only the event id in its failure.
/// </summary>
internal sealed class FlakyIntegrationEventHandler(FlakySwitch flakySwitch) : IIntegrationEventHandler<EmailVerificationRequestedIntegrationEvent>
{
    public Task HandleAsync(EmailVerificationRequestedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (flakySwitch.FailNext)
        {
            flakySwitch.FailNext = false;
            throw new InvalidOperationException($"Flaky integration event handler failed for {integrationEvent.Id}.");
        }

        return Task.CompletedTask;
    }
}
