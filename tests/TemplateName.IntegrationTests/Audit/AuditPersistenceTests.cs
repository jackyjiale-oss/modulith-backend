using System.Data;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Audit.Application;
using TemplateName.Modules.Audit.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Audit;

/// <summary>The <c>audit</c> schema as the host migrates it, the module's permission as the Auth seeder syncs it, and its readiness check.</summary>
public sealed class AuditPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int SqlCheckConstraintViolation = 547;

    private readonly List<AsyncServiceScope> _scopes = [];

    [Fact]
    public async Task Host_migrates_the_audit_schema()
    {
        var context = NewScope().GetRequiredService<AuditDbContext>();

        // The trail and the inbox; no outbox, because the module raises no domain events.
        (await StringsAsync(context, "SELECT name FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'audit'"))
            .ShouldBe(["AuditTrails", "InboxMessages", "__EFMigrationsHistory"], ignoreOrder: true);

        (await ColumnsAsync(context, "AuditTrails")).ShouldBe(new Dictionary<string, string>
        {
            ["Id"] = "bigint identity",
            ["ChangeSetId"] = "uniqueidentifier",
            ["OccurredAt"] = "datetime2(3)",
            ["UserId"] = "uniqueidentifier null",
            ["SessionId"] = "uniqueidentifier null",
            ["Module"] = "nvarchar(64)",
            ["EntityType"] = "nvarchar(128)",
            ["EntityId"] = "nvarchar(200)",
            ["Action"] = "varchar(16)",
            ["Changes"] = "nvarchar(max)",
            ["TraceId"] = "nvarchar(64) null",
        });
        (await StringsAsync(context, "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('audit.InboxMessages')"))
            .ShouldBe(["PK_InboxMessages"]);
    }

    [Fact]
    public async Task Changes_that_are_not_json_are_refused_by_the_check_constraint()
    {
        var context = NewScope().GetRequiredService<AuditDbContext>();

        var exception = await Should.ThrowAsync<SqlException>(() => InsertTrailAsync(context, "not json"));

        exception.Number.ShouldBe(SqlCheckConstraintViolation);
        await InsertTrailAsync(context, """{"Name":{"new":"Reviewer"}}""");
        (await StringsAsync(context, "SELECT Changes FROM audit.AuditTrails")).ShouldBe(["""{"Name":{"new":"Reviewer"}}"""]);
    }

    [Fact]
    public async Task AuditTrails_has_the_four_indexes_of_the_design()
    {
        var context = NewScope().GetRequiredService<AuditDbContext>();

        // Key columns in order, ascending (read backwards for the other direction), Id last as the tie-breaker; nothing unique or filtered.
        var indexes = await StringsAsync(
            context,
            """
            SELECT CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END + ISNULL(i.filter_definition + ' ', '') + '(' + STRING_AGG(
                c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) + ')'
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('audit.AuditTrails') AND i.is_primary_key = 0
            GROUP BY i.index_id, i.is_unique, i.filter_definition
            """);

        indexes.ShouldBe(
            ["(EntityType, EntityId, OccurredAt, Id)", "(UserId, OccurredAt, Id)", "(ChangeSetId)", "(OccurredAt, Id)"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Trails_view_permission_is_seeded_and_held_by_super_admin_only()
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();

        var permission = await context.Set<Permission>().AsNoTracking().SingleAsync(candidate => candidate.Code == AuditPermissions.TrailsView, Ct);

        permission.Code.ShouldBe("audit.trails.view");
        permission.Module.ShouldBe("audit");
        permission.IsDeprecated.ShouldBeFalse();
        var holders = await context.Set<Role>().AsNoTracking()
            .Where(role => role.Permissions.Any(grant => grant.PermissionId == permission.Id))
            .Select(role => role.Name)
            .ToListAsync(Ct);
        holders.ShouldBe([SystemRoles.SuperAdmin]);
        holders.ShouldNotContain(SystemRoles.Admin);
    }

    [Fact]
    public async Task Ready_includes_the_audit_database_check()
    {
        using var response = await Client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var registrations = Factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.ShouldContain(registration => registration.Name == AuditDbContext.Schema && registration.Tags.Contains("ready"));
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static Task InsertTrailAsync(AuditDbContext context, string changes)
        => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO audit.AuditTrails (ChangeSetId, OccurredAt, Module, EntityType, EntityId, Action, Changes)
            VALUES ({Guid.NewGuid()}, SYSUTCDATETIME(), 'sample', 'LeaveRequest', '1', 'Created', {changes})
            """,
            Ct);

    /// <summary>Each column of <c>audit.{table}</c> as its SQL type, with <c>identity</c> and <c>null</c> where they apply.</summary>
    private static async Task<Dictionary<string, string>> ColumnsAsync(AuditDbContext context, string table)
    {
        var rows = await StringsAsync(
            context,
            $"""
            SELECT c.name + '|' + CASE
                WHEN t.name IN ('nvarchar', 'nchar') THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                WHEN t.name IN ('varchar', 'char') THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                WHEN t.name = 'datetime2' THEN 'datetime2(' + CAST(c.scale AS varchar(10)) + ')'
                ELSE t.name END
                + CASE WHEN c.is_identity = 1 THEN ' identity' ELSE '' END
                + CASE WHEN c.is_nullable = 1 THEN ' null' ELSE '' END
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('audit.{table}')
            """);

        return rows.Select(row => row.Split('|')).ToDictionary(parts => parts[0], parts => parts[1]);
    }

    private static async Task<List<string>> StringsAsync(AuditDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }
}
