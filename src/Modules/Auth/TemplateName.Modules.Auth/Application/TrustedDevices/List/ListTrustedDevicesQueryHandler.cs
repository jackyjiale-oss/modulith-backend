using System.Data;
using System.Globalization;
using Dapper;
using TemplateName.Application.Common.Data;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.List;

/// <summary>
/// Pages the caller's trusted devices by keyset (ADR 0010). The SQL is assembled only from constants and <see cref="KeysetQuery"/>
/// clauses, whose column names come from <see cref="TrustedDeviceSortFields"/>; every value is a parameter. Dapper bypasses the EF Core
/// model, so the query itself keeps to the caller's rows (<c>UserId</c>) and to the devices that could still skip the challenge, the rules
/// of <c>TrustedDevice.IsUsable</c> written in SQL: not revoked, before the absolute expiry (a device is over from its expiry instant on),
/// and bound to the security stamp the user holds now (<c>auth.Users.SecurityStamp</c>, compared exactly), so the devices a password
/// change or reset, an MFA change or a suspension forgot are left out although their rows stay until the cleanup (ADR 0025). The cursor
/// is bound to the caller. A caller without a user id gets an empty page.
/// </summary>
internal sealed class ListTrustedDevicesQueryHandler(IDbConnectionFactory connectionFactory, ICurrentUser currentUser, TimeProvider timeProvider)
    : IQueryHandler<ListTrustedDevicesQuery, CursorPage<TrustedDeviceResponse>>
{
    // Same boundaries as the domain: the device is over from its expiry instant on (now >= ExpiresAt), and the stamps compare ordinally
    // (a binary collation, like StringComparison.Ordinal in TrustedDevice.IsUsable).
    private const string Filter = """
        UserId = @UserId
        AND RevokedAt IS NULL
        AND ExpiresAt > @Now
        AND EXISTS (
            SELECT 1
            FROM [auth].[Users] AS owner
            WHERE owner.Id = [auth].[TrustedDevices].UserId
              AND owner.SecurityStamp = [auth].[TrustedDevices].SecurityStamp COLLATE Latin1_General_BIN2)
        """;

    public async Task<Result<CursorPage<TrustedDeviceResponse>>> HandleAsync(ListTrustedDevicesQuery query, CancellationToken cancellationToken)
    {
        var sortResult = SortSpecification.Parse(
            query.Page.Sort,
            TrustedDeviceSortFields.Allowed,
            TrustedDeviceSortFields.Id,
            TrustedDeviceSortFields.Default);
        if (sortResult.IsFailure)
        {
            return sortResult.Error;
        }

        var sort = sortResult.Value;
        var userId = currentUser.UserId ?? Guid.Empty;
        var filterHash = CursorCodec.ComputeFilterHash(string.Create(CultureInfo.InvariantCulture, $"userId={userId}"));

        Cursor? cursor = null;
        if (!string.IsNullOrEmpty(query.Page.Cursor))
        {
            var cursorResult = CursorCodec.Decode(query.Page.Cursor, sort, filterHash);
            if (cursorResult.IsFailure)
            {
                return cursorResult.Error;
            }

            cursor = cursorResult.Value;
        }

        var keyset = KeysetSqlBuilder.Build(sort, cursor, query.Page.PageSize);
        var parameters = keyset.Parameters;
        parameters.Add("Take", keyset.Take);
        parameters.Add("UserId", userId, DbType.Guid);
        // datetime2 like the columns (Dapper's default DateTime is datetime, which rounds and cannot use the index as well).
        parameters.Add("Now", timeProvider.GetUtcNow().UtcDateTime, DbType.DateTime2);

        // The columns follow the TrustedDeviceResponse constructor, which Dapper binds by position.
        var pageSql = $"""
            SELECT TOP (@Take) Id, DeviceName, UserAgent, CreatedAt, ExpiresAt, LastUsedAt
            FROM [auth].[TrustedDevices]
            WHERE {Filter} AND {keyset.WhereClause}
            ORDER BY {keyset.OrderByClause}
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TrustedDeviceResponse>(new CommandDefinition(pageSql, parameters, cancellationToken: cancellationToken));

        long? totalCount = null;
        if (query.Page.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT_BIG(*) FROM [auth].[TrustedDevices] WHERE {Filter}";
            totalCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));
        }

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC. The cursor keys are taken from these rows, so they
        // carry exactly the millisecond values SQL Server stored.
        var items = rows.Select(row => row with
        {
            CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            ExpiresAt = DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc),
            LastUsedAt = row.LastUsedAt is { } lastUsedAt ? DateTime.SpecifyKind(lastUsedAt, DateTimeKind.Utc) : null,
        }).ToList();

        return CursorPageBuilder.Build(
            items,
            keyset,
            cursor,
            sort,
            filterHash,
            item => [.. sort.Terms.Select(term => TrustedDeviceSortFields.ValueOf(item, term.Field))],
            totalCount);
    }
}
