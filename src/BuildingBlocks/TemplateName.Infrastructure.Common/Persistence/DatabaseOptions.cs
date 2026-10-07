namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Database behavior (section <c>Database</c>).</summary>
public sealed class DatabaseOptions
{
    internal const string SectionName = "Database";

    /// <summary>
    /// Migrates every module database when the host starts. Meant for Development only; other environments deploy migrations as EF
    /// migration bundles (ADR 0006).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
