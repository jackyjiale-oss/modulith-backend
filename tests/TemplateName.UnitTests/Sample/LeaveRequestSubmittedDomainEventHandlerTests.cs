using Microsoft.Extensions.Logging;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;
using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Sample;

public sealed class LeaveRequestSubmittedDomainEventHandlerTests
{
    [Fact]
    public async Task Logs_the_submitted_request_at_information()
    {
        var logger = new RecordingLogger<LeaveRequestSubmittedDomainEventHandler>();
        var sut = new LeaveRequestSubmittedDomainEventHandler(logger);
        var leaveRequestId = Guid.NewGuid();

        await sut.HandleAsync(
            new LeaveRequestSubmittedDomainEvent(leaveRequestId, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        var entry = logger.Entries.Single();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe($"Leave request {leaveRequestId} submitted");
    }
}
