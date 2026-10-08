namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// The names of the unique indexes whose refusal a handler turns into its own answer (<see cref="IUnitOfWork.SaveChangesUnlessDuplicateAsync(string, CancellationToken)"/>).
/// The persistence configuration gives the index exactly this name, so the two cannot drift apart.
/// </summary>
internal static class UniqueIndexNames
{
    /// <summary>The unique index on <c>auth.Roles.NormalizedName</c> (filtered to roles that are not deleted).</summary>
    public const string RoleName = "IX_Roles_NormalizedName";
}
