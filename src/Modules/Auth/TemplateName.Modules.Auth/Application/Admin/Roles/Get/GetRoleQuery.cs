using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.Get;

/// <summary>Reads the role <paramref name="RoleId"/> with its permissions.</summary>
internal sealed record GetRoleQuery(Guid RoleId) : IQuery<RoleResponse>;
