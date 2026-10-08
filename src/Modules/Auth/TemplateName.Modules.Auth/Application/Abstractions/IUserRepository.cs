using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Loads and adds <see cref="User"/> aggregates, each with its password history and role assignments. Soft-deleted users are not found.</summary>
internal interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a user by <see cref="User.NormalizedEmail"/> (see <see cref="User.NormalizeEmail"/>).</summary>
    Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    void Add(User user);
}
