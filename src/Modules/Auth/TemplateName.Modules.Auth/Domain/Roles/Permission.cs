using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Roles;

/// <summary>
/// A permission declared by a module (see <c>IPermissionSource</c>) and synced into the table at startup. A permission that is no
/// longer declared is deprecated, not deleted, so existing grants stay readable.
/// </summary>
internal sealed class Permission : Entity<Guid>
{
    // EF Core materializes the entity through this constructor; callers use Create.
    private Permission()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Module { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public bool IsDeprecated { get; private set; }

    public static Permission Create(string code, string module, string name, string description, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Create(now),
        Code = code,
        Module = module,
        Name = name,
        Description = description,
    };

    public void Describe(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public void SetDeprecated(bool isDeprecated) => IsDeprecated = isDeprecated;
}
