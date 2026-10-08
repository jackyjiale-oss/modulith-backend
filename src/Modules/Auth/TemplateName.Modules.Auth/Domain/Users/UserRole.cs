using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>The assignment of a role to a user.</summary>
internal sealed class UserRole
{
    // EF Core materializes the entity through this constructor; the user creates assignments.
    private UserRole()
    {
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid? AssignedBy { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    internal static UserRole Create(Guid userId, Guid roleId, Guid? assignedBy, DateTimeOffset now) => new()
    {
        UserId = userId,
        RoleId = roleId,
        AssignedBy = assignedBy,
        AssignedAt = now,
    };
}
