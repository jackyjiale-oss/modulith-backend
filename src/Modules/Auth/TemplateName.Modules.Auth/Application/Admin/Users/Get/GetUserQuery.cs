using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Admin.Users.Get;

/// <summary>One user as an administrator sees it.</summary>
internal sealed record GetUserQuery(Guid UserId) : IQuery<UserResponse>;
