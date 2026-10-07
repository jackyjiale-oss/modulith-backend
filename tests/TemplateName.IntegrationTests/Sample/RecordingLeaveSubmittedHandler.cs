using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Outbox;
using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;

namespace TemplateName.IntegrationTests.Sample;

/// <summary>Records every <see cref="LeaveRequestSubmittedDomainEvent"/> the Sample outbox dispatches.</summary>
internal sealed class RecordingLeaveSubmittedHandler(EventRecorder recorder) : IDomainEventHandler<LeaveRequestSubmittedDomainEvent>
{
    public Task HandleAsync(LeaveRequestSubmittedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        recorder.Record(domainEvent);
        return Task.CompletedTask;
    }
}
