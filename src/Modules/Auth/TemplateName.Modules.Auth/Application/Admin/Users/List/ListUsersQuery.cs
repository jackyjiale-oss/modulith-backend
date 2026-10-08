using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Admin.Users.List;

/// <summary>A page of users, optionally only those whose email starts with <paramref name="Search"/> and/or with one status.</summary>
internal sealed record ListUsersQuery(string? Search, UserStatus? Status, CursorPageRequest Page) : IQuery<CursorPage<UserListItemResponse>>;
