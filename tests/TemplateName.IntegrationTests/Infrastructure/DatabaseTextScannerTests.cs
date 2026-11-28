using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>The scanner reports where a secret was stored, never the migration history that every module's schema carries.</summary>
public sealed class DatabaseTextScannerTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task A_value_found_only_in_a_migration_id_is_not_reported()
    {
        string migrationId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            migrationId = (await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetAppliedMigrationsAsync(Ct)).First();
        }

        // The timestamp of a migration ID: the digits a random six-digit code can collide with.
        var timestamp = migrationId[..14];

        (await DatabaseTextScanner.ColumnsContainingAsync(Factory.Services, [timestamp], Ct)).ShouldBeEmpty();
    }
}
