namespace TemplateName.Application.Common.Identity;

/// <summary>The user making the current request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    bool IsAuthenticated { get; }
}
