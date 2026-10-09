using System.Text.Json;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.Modules.Auth.Application.Admin.Users;

/// <summary>The audit entries of the user administration: the target in <c>UserId</c>, the acting administrator in <c>Details</c>.</summary>
internal static class AdminAudit
{
    /// <summary>A successful administration action: <paramref name="eventType"/> on <paramref name="targetId"/> by <paramref name="actorId"/>.</summary>
    public static AuthAuditLog Succeeded(string eventType, Guid targetId, Guid actorId, DateTimeOffset now)
        => AuthAuditLog.Create(eventType, succeeded: true, now, userId: targetId, details: Details(actorId));

    /// <summary><c>{"actorId":"…"}</c>, written by the JSON serializer so the column always holds valid JSON.</summary>
    public static string Details(Guid actorId) => JsonSerializer.Serialize(new { actorId });
}
