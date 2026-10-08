namespace TemplateName.Application.Common.Identity;

/// <summary>Answers whether a user holds a permission. The Auth module implements it from the user's roles, with a short-lived cache.</summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken);
}
