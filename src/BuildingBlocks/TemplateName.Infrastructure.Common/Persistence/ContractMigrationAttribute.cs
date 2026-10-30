namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Marks an EF Core migration whose <c>Up</c> drops or renames a column or table (the contract step of expand and contract). A migration
/// that does so without this marker fails the architecture tests, and so does a marker on a migration that contracts nothing. The reason
/// names the release that stopped using the column or table.
/// </summary>
/// <param name="reason">Why the contraction is safe now; not null, empty or whitespace.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ContractMigrationAttribute(string reason) : Attribute
{
    public string Reason { get; } = Validated(reason);

    private static string Validated(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason;
    }
}
