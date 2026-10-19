using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Mfa.GetMethods;

/// <summary>
/// Reads the caller's methods and unused recovery codes through Dapper. Dapper bypasses the EF Core model, so both statements keep to
/// the caller's rows themselves (<c>UserId = @UserId</c>), and neither selects <c>SecretEncrypted</c> or a code hash. A user has at most
/// one method (one per type, plan decision D18), so the list is not paged. A caller without a user id gets an empty answer.
/// </summary>
internal sealed class GetMfaMethodsQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser)
    : IQueryHandler<GetMfaMethodsQuery, MfaMethodsResponse>
{
    private const string Sql = """
        SELECT Id, Type, Label, CAST(CASE WHEN ConfirmedAt IS NULL THEN 0 ELSE 1 END AS bit) AS Confirmed, CreatedAt, ConfirmedAt, LastUsedAt
        FROM auth.UserMfaMethods
        WHERE UserId = @UserId
        ORDER BY Type, Id;

        SELECT COUNT(*)
        FROM auth.RecoveryCodes
        WHERE UserId = @UserId AND UsedAt IS NULL;
        """;

    public async Task<Result<MfaMethodsResponse>> HandleAsync(GetMfaMethodsQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;

        List<MethodRow> rows;
        int recoveryCodesRemaining;
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
                Sql,
                new { UserId = userId },
                cancellationToken: cancellationToken));
            rows = [.. await results.ReadAsync<MethodRow>()];
            recoveryCodesRemaining = await results.ReadSingleAsync<int>();
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC.
        var methods = rows
            .Select(row => new MfaMethodResponse(
                row.Id,
                (MfaMethodType)row.Type,
                row.Label,
                row.Confirmed,
                DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
                AsUtc(row.ConfirmedAt),
                AsUtc(row.LastUsedAt)))
            .ToList();

        return new MfaMethodsResponse(methods.Exists(method => method.Confirmed), recoveryCodesRemaining, methods.AsReadOnly());
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;

    /// <summary>The method columns, bound by Dapper through the constructor in column order.</summary>
    private sealed record MethodRow(Guid Id, int Type, string Label, bool Confirmed, DateTime CreatedAt, DateTime? ConfirmedAt, DateTime? LastUsedAt);
}
