using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>What one <see cref="PermissionSynchronizer"/> run found, for the seeder to turn into grants.</summary>
/// <param name="Active">Every permission that is not deprecated after the run: what <c>SuperAdmin</c> holds.</param>
/// <param name="AdminDefaults">
/// The active permissions whose definition is flagged <c>AdminDefault</c>: what a freshly created <c>Admin</c> role starts with.
/// </param>
/// <param name="InsertedAdminDefaults">
/// The subset of <paramref name="AdminDefaults"/> whose code this run inserted: what an existing <c>Admin</c> role receives. A code that
/// was only updated or re-declared after being deprecated is not in it, so a grant an administrator removed is never added back.
/// </param>
internal sealed record PermissionSyncResult(
    IReadOnlyList<Permission> Active,
    IReadOnlyList<Permission> AdminDefaults,
    IReadOnlyList<Permission> InsertedAdminDefaults);
