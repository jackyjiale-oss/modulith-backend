using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Submit;

/// <summary>Placeholder reaction to a submitted request; the Notifications plan replaces the log line with a notification.</summary>
internal sealed partial class LeaveRequestSubmittedDomainEventHandler(
    ILogger<LeaveRequestSubmittedDomainEventHandler> logger) : IDomainEventHandler<LeaveRequestSubmittedDomainEvent>
{
    public Task HandleAsync(LeaveRequestSubmittedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        LogSubmitted(logger, domainEvent.LeaveRequestId);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Leave request {LeaveRequestId} submitted")]
    private static partial void LogSubmitted(ILogger logger, Guid leaveRequestId);
}
