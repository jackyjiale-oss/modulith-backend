namespace TemplateName.Modules.Auth.Application.Admin.Users.Get;

/// <summary>One role of a user: the id <c>PUT .../roles</c> takes and the name to show.</summary>
internal sealed record UserRoleResponse(Guid Id, string Name);
