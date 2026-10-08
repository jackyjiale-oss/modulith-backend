using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="Permission"/> rows, including deprecated ones.</summary>
internal interface IPermissionRepository
{
    /// <summary>Every permission, tracked, so the startup sync can change them and save.</summary>
    Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken);

    /// <summary>How many of <paramref name="ids"/> name an existing permission (duplicates in the input count once).</summary>
    Task<int> CountExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    void Add(Permission permission);
}
