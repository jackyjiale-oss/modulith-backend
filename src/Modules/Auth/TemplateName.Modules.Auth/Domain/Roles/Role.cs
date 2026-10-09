using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Roles;

/// <summary>
/// A named set of permissions that can be assigned to users. System roles (<see cref="SystemRoles"/>) are seeded: they cannot be
/// renamed or deleted, and the permission set of <see cref="SystemRoles.SuperAdmin"/> is maintained by the seeder only.
/// </summary>
internal sealed class Role : AggregateRoot<Guid>, IAuditable, ISoftDeletable
{
    /// <summary>The column limit of <see cref="Name"/>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The column limit of <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 500;

    private readonly List<RolePermission> _permissions = [];

    // EF Core materializes the aggregate through this constructor; callers use Create or CreateSystem.
    private Role()
    {
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The form of the name used for lookups and the unique index: trimmed and upper-case.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public byte[] RowVersion { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    /// <summary>
    /// Whether this is the seeded <see cref="SystemRoles.SuperAdmin"/> role: a system role whose <see cref="NormalizedName"/> is that
    /// name's, so no spelling of the name escapes the protection, and a custom role never gets it by taking the name.
    /// </summary>
    public bool IsSuperAdmin => IsSystem && string.Equals(NormalizedName, NormalizeName(SystemRoles.SuperAdmin), StringComparison.Ordinal);

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    /// <summary>Creates a custom role. A blank name is a programming error: the application validator rejects it with a field error first.</summary>
    public static Result<Role> Create(string name, string description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Build(name, description, isSystem: false, now);
    }

    public static Role CreateSystem(string name, string description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Build(name, description, isSystem: true, now);
    }

    public Result UpdateDetails(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (IsSystem)
        {
            return Result.Failure(RoleErrors.SystemRoleProtected);
        }

        Name = name.Trim();
        NormalizedName = NormalizeName(name);
        Description = description;

        return Result.Success();
    }

    /// <summary>Replaces the permission set. Idempotent; duplicates are ignored. Refused for the SuperAdmin role.</summary>
    public Result SetPermissions(IReadOnlyCollection<Guid> permissionIds)
    {
        if (IsSuperAdmin)
        {
            return Result.Failure(RoleErrors.SystemRoleProtected);
        }

        SyncPermissions(permissionIds);

        return Result.Success();
    }

    /// <summary>Replaces the permission set without protection. For the seeder, which keeps SuperAdmin on every declared permission.</summary>
    public void SyncPermissions(IReadOnlyCollection<Guid> permissionIds)
    {
        var wanted = permissionIds.ToHashSet();

        _permissions.RemoveAll(grant => !wanted.Contains(grant.PermissionId));
        foreach (var permissionId in wanted)
        {
            if (!_permissions.Exists(grant => grant.PermissionId == permissionId))
            {
                _permissions.Add(RolePermission.Create(Id, permissionId));
            }
        }
    }

    /// <summary>
    /// Checks that the role may be deleted and, when it may, drops its grants: a deleted role grants nothing, and a new role that takes
    /// its freed name does not inherit them. The caller removes the role from its repository; the save interceptor turns that deletion
    /// into the soft-delete flags (the grants themselves are deleted for good).
    /// </summary>
    public Result MarkDeleted()
    {
        if (IsSystem)
        {
            return Result.Failure(RoleErrors.SystemRoleProtected);
        }

        _permissions.Clear();

        return Result.Success();
    }

    private static Role Build(string name, string description, bool isSystem, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Create(now),
        Name = name.Trim(),
        NormalizedName = NormalizeName(name),
        Description = description,
        IsSystem = isSystem,
    };
}
