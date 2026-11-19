using TemplateName.Application.Common.Concurrency;

namespace TemplateName.Modules.Auth.Application.Admin.Users;

/// <summary>
/// The version of a user as administered through <c>PUT</c> user roles (ADR 0038): the ids of the user's roles that are not deleted.
/// It covers nothing a sign-in or the POST state changes write, so a user signing in does not invalidate an administrator's copy.
/// </summary>
internal static class AdministeredUserVersion
{
    public static EntityTag Of(IEnumerable<Guid> roleIds) => new EntityTagBuilder("user-roles").AddSet(roleIds).Build();
}
