using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.IntegrationTests.UpgradePath;

/// <summary>What the seeds of the upgrade-path ladder must satisfy; they take no database.</summary>
internal static class UpgradeLadderRules
{
    /// <summary>
    /// The rules, as one message each: a seed names a migration that exists (a renamed migration cannot leave a stale seed), a seed and
    /// its verify script come as a pair, and a <see cref="ContractMigrationAttribute"/> migration has a seed on the migration before it,
    /// so the contraction is proved over data.
    /// </summary>
    /// <param name="ladders">The migrations of every context.</param>
    /// <param name="seedKeys">The keys (<c>{Module}/{Name}</c>) of the seed scripts.</param>
    /// <param name="verifyKeys">The keys of the verify scripts.</param>
    public static IReadOnlyList<string> Violations(IReadOnlyList<ModuleLadder> ladders, ISet<string> seedKeys, ISet<string> verifyKeys)
    {
        ArgumentNullException.ThrowIfNull(ladders);
        ArgumentNullException.ThrowIfNull(seedKeys);
        ArgumentNullException.ThrowIfNull(verifyKeys);

        var violations = new List<string>();
        var migrationKeys = ladders.SelectMany(ladder => ladder.Rungs.Select(rung => KeyOf(ladder, rung))).ToHashSet(StringComparer.Ordinal);

        foreach (var key in seedKeys.Order(StringComparer.Ordinal))
        {
            if (!migrationKeys.Contains(key))
            {
                violations.Add($"The seed '{key}' names no migration; rename or delete it (a renamed migration leaves its seed behind).");
            }

            if (!verifyKeys.Contains(key))
            {
                violations.Add($"The seed '{key}' has no verify script; add '{key}.verify.sql'.");
            }
        }

        foreach (var key in verifyKeys.Order(StringComparer.Ordinal).Where(key => !seedKeys.Contains(key)))
        {
            violations.Add($"The verify script '{key}' has no seed; add '{key}.seed.sql'.");
        }

        foreach (var ladder in ladders)
        {
            for (var index = 1; index < ladder.Rungs.Count; index++)
            {
                var rung = ladder.Rungs[index];
                var before = ladder.Rungs[index - 1];
                if (rung.MigrationType.IsDefined(typeof(ContractMigrationAttribute), inherit: false) && !seedKeys.Contains(KeyOf(ladder, before)))
                {
                    violations.Add(
                        $"The contract migration '{KeyOf(ladder, rung)}' has no seed on the migration before it; add '{KeyOf(ladder, before)}.seed.sql' " +
                        "and its verify script, so the contraction is proved over data.");
                }
            }
        }

        return violations;
    }

    private static string KeyOf(ModuleLadder ladder, MigrationRung rung) => $"{ladder.Module}/{rung.Name}";
}
