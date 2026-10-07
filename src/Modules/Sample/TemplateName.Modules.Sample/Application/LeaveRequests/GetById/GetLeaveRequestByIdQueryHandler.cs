using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.GetById;

internal sealed class GetLeaveRequestByIdQueryHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse>
{
    // Dapper bypasses the EF Core soft-delete filter, so the query repeats it (ADR 0006). The columns follow the
    // LeaveRequestResponse constructor, which Dapper binds by position.
    private const string Sql = """
        SELECT Id, EmployeeId, StartDate, EndDate, Reason, Status, ApproverId, CreatedAt
        FROM sample.LeaveRequests
        WHERE Id = @Id AND IsDeleted = 0
        """;

    public async Task<Result<LeaveRequestResponse>> HandleAsync(GetLeaveRequestByIdQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var leaveRequest = await connection.QuerySingleOrDefaultAsync<LeaveRequestResponse>(
            new CommandDefinition(Sql, new { Id = query.LeaveRequestId }, cancellationToken: cancellationToken));

        if (leaveRequest is null)
        {
            return Result.Failure<LeaveRequestResponse>(LeaveRequestErrors.NotFound(query.LeaveRequestId));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the column holds UTC, so say so before it is serialized.
        return leaveRequest with { CreatedAt = DateTime.SpecifyKind(leaveRequest.CreatedAt, DateTimeKind.Utc) };
    }
}
