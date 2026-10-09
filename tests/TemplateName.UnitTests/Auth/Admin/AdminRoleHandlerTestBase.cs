using System.Text.Json;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Admin.Roles;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.UnitTests.Auth.Admin;

/// <summary>
/// What the role administration handlers share on top of <see cref="AdminHandlerTestBase"/>: roles the repository finds by id, the
/// users a role is assigned to, and the real cache invalidator over the fake repository and cache. The unique-name save succeeds unless
/// a test says otherwise.
/// </summary>
public abstract class AdminRoleHandlerTestBase : AdminHandlerTestBase
{
    private protected AdminRoleHandlerTestBase()
    {
        Roles.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => StoredRoles.SingleOrDefault(role => role.Id == call.Arg<Guid>()));
        Roles.GetUserIdsInRoleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Guid>)[.. UsersInRole]);
        UnitOfWork.SaveChangesUnlessDuplicateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        CacheInvalidator = new RolePermissionCacheInvalidator(Roles, PermissionCache);
    }

    internal RolePermissionCacheInvalidator CacheInvalidator { get; }

    internal List<Role> StoredRoles { get; } = [];

    /// <summary>The ids of the users the repository reports for any role.</summary>
    internal List<Guid> UsersInRole { get; } = [];

    /// <summary>A custom role the repository finds by id.</summary>
    internal Role GivenStoredRole(string name, params Permission[] granted)
    {
        var role = Role.Create(name, $"The {name} role.", Now.AddDays(-5)).Value;
        role.SetPermissions([.. granted.Select(permission => permission.Id)]);
        StoredRoles.Add(role);
        return role;
    }

    /// <summary>A system role the repository finds by id.</summary>
    internal Role GivenStoredSystemRole(string name)
    {
        var role = Role.CreateSystem(name, $"The {name} role.", Now.AddDays(-30));
        StoredRoles.Add(role);
        return role;
    }

    /// <summary>The members of an audit entry's <c>Details</c>, which must be valid JSON.</summary>
    internal static JsonElement DetailsOf(AuthAuditLog entry)
    {
        using var document = JsonDocument.Parse(entry.Details!);
        return document.RootElement.Clone();
    }
}
