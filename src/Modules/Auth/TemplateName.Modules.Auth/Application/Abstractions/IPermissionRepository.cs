using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="Permission"/> rows, including deprecated ones.</summary>
internal interface IPermissionRepository
{
    /// <summary>Every permission, tracked, so the startup sync can change them and save.</summary>
    Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The permissions among <paramref name="ids"/> that exist, deprecated ones included; unknown ids are left out.</summary>
    Task<IReadOnlyList<Permission>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    void Add(Permission permission);
}
