using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Create;

/// <summary>
/// Creates a custom role with no permissions. The name is unique ignoring case and surrounding white space
/// (<see cref="RoleErrors.NameTaken"/>); the lookup gives the answer in the usual case, and when two creates of one name race past it
/// the unique index <see cref="UniqueIndexNames.RoleName"/> refuses the second insert, which
/// <see cref="IUnitOfWork.SaveChangesUnlessDuplicateAsync(string, CancellationToken)"/> turns into the same answer instead of a 500
/// (any other unique violation is still an error). Audited as <c>auth.role_created</c> in the same save.
/// </summary>
internal sealed class CreateRoleCommandHandler(
    IRoleRepository roles,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<CreateRoleCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (await roles.NameExistsAsync(Role.NormalizeName(name), exceptId: null, cancellationToken))
        {
            return RoleErrors.NameTaken(name);
        }

        var now = timeProvider.GetUtcNow();
        var role = Role.Create(name, command.Description.Trim(), now).Value;
        roles.Add(role);
        auditWriter.Record(RoleAudit.Created(command.ActorId, role, now));

        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, cancellationToken))
        {
            return RoleErrors.NameTaken(name);
        }

        return role.Id;
    }
}
