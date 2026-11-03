namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>The statement that drops and re-creates a test database, built so that any database name is valid SQL.</summary>
internal static class EmptyDatabaseSql
{
    /// <summary>
    /// Drops the database <paramref name="name"/> when it exists and creates it again. The name is escaped where it appears: a single
    /// quote is doubled inside the <c>N'...'</c> literal and a closing bracket inside every <c>[...]</c>, so a generated project's name
    /// (<c>Acme.Hr_MigrateCommand</c>, with a dot, a hyphen or a space) works as well as a plain identifier.
    /// </summary>
    public static string Build(string name)
    {
        var literal = name.Replace("'", "''", StringComparison.Ordinal);
        var quoted = $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
        return $"IF DB_ID(N'{literal}') IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END; CREATE DATABASE {quoted};";
    }
}
