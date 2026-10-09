using System.Text.Json;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Admin.Roles;

/// <summary>
/// The audit entries of the role administration. A role is not a user, so <c>UserId</c> stays empty (the log's <c>userId</c> filter finds
/// the events <em>about</em> a user, as for the user administration); the acting administrator is <c>actorId</c> in <c>Details</c>, next
/// to the role. <c>Details</c> is written by the JSON serializer, so the column always holds valid JSON, and holds no secret: only ids,
/// the role's name and permission codes.
/// </summary>
internal static class RoleAudit
{
    public static AuthAuditLog Created(Guid actorId, Role role, DateTimeOffset now)
        => Succeeded(AuthAuditEvents.RoleCreated, now, new { actorId, roleId = role.Id, name = role.Name });

    public static AuthAuditLog Updated(Guid actorId, Role role, string previousName, DateTimeOffset now)
        => Succeeded(AuthAuditEvents.RoleUpdated, now, new { actorId, roleId = role.Id, name = role.Name, previousName });

    public static AuthAuditLog Deleted(Guid actorId, Role role, DateTimeOffset now)
        => Succeeded(AuthAuditEvents.RoleDeleted, now, new { actorId, roleId = role.Id, name = role.Name });

    /// <summary>The permission codes that were granted and withdrawn, in code order.</summary>
    public static AuthAuditLog PermissionsChanged(Guid actorId, Role role, IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed, DateTimeOffset now)
        => Succeeded(AuthAuditEvents.RolePermissionsChanged, now, new { actorId, roleId = role.Id, name = role.Name, added, removed });

    private static AuthAuditLog Succeeded(string eventType, DateTimeOffset now, object details)
        => AuthAuditLog.Create(eventType, succeeded: true, now, details: JsonSerializer.Serialize(details));
}
