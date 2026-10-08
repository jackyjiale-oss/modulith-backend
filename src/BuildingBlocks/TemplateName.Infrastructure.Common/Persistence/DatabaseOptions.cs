namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Database behavior (section <c>Database</c>).</summary>
public sealed class DatabaseOptions
{
    internal const string SectionName = "Database";

    /// <summary>
    /// Migrates every module database when the host starts. Set it in Development only; production migrates through the API's
    /// <c>migrate</c> mode (ADR 0006).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
