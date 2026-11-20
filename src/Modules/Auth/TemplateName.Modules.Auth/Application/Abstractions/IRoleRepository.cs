using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="Role"/> aggregates, each with its permission grants. Soft-deleted roles are not found.</summary>
internal interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a role by <see cref="Role.NormalizedName"/> (see <see cref="Role.NormalizeName"/>).</summary>
    Task<Role?> GetByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken);

    /// <summary>The roles among <paramref name="ids"/> that exist; unknown ids are left out.</summary>
    Task<IReadOnlyList<Role>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Whether a role other than <paramref name="exceptId"/> already uses the name.</summary>
    Task<bool> NameExistsAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken);

    /// <summary>The users the role is assigned to, for example to clear their cached permissions after a change.</summary>
    Task<IReadOnlyList<Guid>> GetUserIdsInRoleAsync(Guid roleId, CancellationToken cancellationToken);

    void Add(Role role);

    /// <summary>Marks the role as deleted; the save turns that into the soft delete (see <see cref="Role.MarkDeleted"/>).</summary>
    void Remove(Role role);

    /// <summary>
    /// Marks the role row itself as updated (its <c>UpdatedAt</c>), so the next save checks and bumps its <c>RowVersion</c> even when only
    /// its permission links change: of two simultaneous permission saves, the second fails with 409 (ADR 0038). The audit trail records
    /// nothing for it, since <c>UpdatedAt</c> is not an audited column.
    /// </summary>
    void MarkUpdated(Role role);
}
