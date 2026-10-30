using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// Reads the <c>Up</c> operations of EF Core migrations and reports the ones that drop or rename a column or table without
/// <see cref="ContractMigrationAttribute"/>, and the marked ones that do neither. <c>Down</c> is never read and altering a column is not checked.
/// </summary>
internal static class MigrationGuard
{
    public static IReadOnlyList<string> Violations(IEnumerable<Type> migrationTypes)
    {
        var violations = new List<string>();

        foreach (var type in migrationTypes)
        {
            MigrationOperation? contraction;
            try
            {
                var migration = (Migration)Activator.CreateInstance(type, nonPublic: true)!;
                contraction = migration.UpOperations.FirstOrDefault(IsContraction);
            }
            catch (Exception exception)
            {
                // A constructor that throws reaches here wrapped in a TargetInvocationException; report the real cause.
                var cause = exception is TargetInvocationException ? exception.InnerException ?? exception : exception;
                violations.Add($"{type.FullName} could not be read: {cause.Message}");
                continue;
            }

            var marked = type.GetCustomAttribute<ContractMigrationAttribute>(inherit: false) is not null;
            if (contraction is not null && !marked)
            {
                violations.Add($"{type.FullName} drops or renames a column or table ({contraction.GetType().Name}) and has no [ContractMigration]");
            }
            else if (contraction is null && marked)
            {
                violations.Add($"{type.FullName} has [ContractMigration] but drops or renames no column or table");
            }
        }

        return violations;
    }

    private static bool IsContraction(MigrationOperation operation) =>
        operation is DropColumnOperation or RenameColumnOperation or DropTableOperation or RenameTableOperation;
}
