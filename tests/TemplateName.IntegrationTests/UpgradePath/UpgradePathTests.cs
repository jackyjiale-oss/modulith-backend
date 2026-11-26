using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.UpgradePath;

/// <summary>
/// The upgrade-path ladder (docs/deployment/migrations.md): every context migrates one migration at a time on a database of its own,
/// the seeds write rows at the schema they were written for, and once the database is at the latest migration the verify scripts must
/// find every row and every rewritten value.
/// </summary>
public sealed class UpgradePathTests(IntegrationTestWebAppFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_context_migrates_over_seeded_data()
    {
        var connectionString = await factory.CreateEmptyDatabaseAsync("TemplateName_UpgradePath", Ct);
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", connectionString));
        await using var scope = host.Services.CreateAsyncScope();
        var registered = scope.ServiceProvider.GetServices<ModuleDbContextRegistration>().Select(registration => registration.ContextType).ToHashSet();
        var failures = new List<string>();

        foreach (var ladder in UpgradeLadder.Discover())
        {
            if (!registered.Contains(ladder.ContextType))
            {
                failures.Add($"{ladder.Module}: {ladder.ContextType.Name} is not registered for migrations, so the ladder cannot run it.");
                continue;
            }

            var context = (DbContext)scope.ServiceProvider.GetRequiredService(ladder.ContextType);
            var seeded = await MigrateRungByRungAsync(context, ladder, failures);
            foreach (var rung in ladder.Rungs.Where(rung => seeded.Contains(rung.Name)))
            {
                await VerifyAsync(connectionString, ladder, rung, failures);
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    /// <summary>Migrates to each rung in turn and runs its seed; returns the names of the rungs whose seed ran without an error.</summary>
    private static async Task<HashSet<string>> MigrateRungByRungAsync(DbContext context, ModuleLadder ladder, List<string> failures)
    {
        var migrator = context.GetService<IMigrator>();
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rung in ladder.Rungs)
        {
            var key = $"{ladder.Module}/{rung.Name}";
            try
            {
                await migrator.MigrateAsync(rung.Id, Ct);
            }
#pragma warning disable CA1031 // A failing step is a finding of the test: every one is reported by name, none stops the others.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failures.Add($"{key}: migrating to {rung.Id} failed: {exception.Message}");
                return seeded;
            }

            if (!UpgradeLadder.Seeds.TryGetValue(key, out var seed))
            {
                continue;
            }

            try
            {
                await context.Database.ExecuteSqlRawAsync(seed, Ct);
                seeded.Add(rung.Name);
            }
#pragma warning disable CA1031
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failures.Add($"{key}: the seed '{key}.seed.sql' failed: {exception.Message}");
            }
        }

        // The rungs reach the latest migration; this is the same call the migrate mode makes, and it catches a migration the ladder missed.
        try
        {
            await context.Database.MigrateAsync(Ct);
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            failures.Add($"{ladder.Module}: migrating to the latest migration failed: {exception.Message}");
        }

        return seeded;
    }

    private static async Task VerifyAsync(string connectionString, ModuleLadder ladder, MigrationRung rung, List<string> failures)
    {
        var key = $"{ladder.Module}/{rung.Name}";
        if (!UpgradeLadder.Verifies.TryGetValue(key, out var verify))
        {
            failures.Add($"{key}: the seed has no verify script '{key}.verify.sql'.");
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = verify;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            do
            {
                while (await reader.ReadAsync(Ct))
                {
                    failures.Add($"{key}: {Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
            while (await reader.NextResultAsync(Ct));
        }
        catch (SqlException exception)
        {
            failures.Add($"{key}: the verify script '{key}.verify.sql' failed: {exception.Message}");
        }
    }
}
