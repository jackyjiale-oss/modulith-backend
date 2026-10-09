using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.Roles.List;

/// <summary>A page of the roles that are not deleted.</summary>
internal sealed record ListRolesQuery(CursorPageRequest Page) : IQuery<CursorPage<RoleListItemResponse>>;
