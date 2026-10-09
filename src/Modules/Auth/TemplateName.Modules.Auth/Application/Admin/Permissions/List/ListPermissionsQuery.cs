using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Admin.Permissions.List;

/// <summary>A page of the permissions that modules declare; permissions no module declares any more only when <paramref name="IncludeDeprecated"/>.</summary>
internal sealed record ListPermissionsQuery(bool IncludeDeprecated, CursorPageRequest Page) : IQuery<CursorPage<PermissionResponse>>;
