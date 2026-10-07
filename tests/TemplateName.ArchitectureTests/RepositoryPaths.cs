namespace TemplateName.ArchitectureTests;

/// <summary>Locates the repository on disk, so tests can read the documents that ship with the code. The integration tests link this file.</summary>
public static class RepositoryPaths
{
    /// <summary>The directory that holds the solution (<c>*.slnx</c>), found by walking up from the test binaries.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No directory above '{AppContext.BaseDirectory}' contains a *.slnx solution file.");
    }
}
