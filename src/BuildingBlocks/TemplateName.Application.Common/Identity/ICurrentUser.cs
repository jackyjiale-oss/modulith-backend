namespace TemplateName.Application.Common.Identity;

/// <summary>The user making the current request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    /// <summary>The session the caller's access token belongs to (the <c>sid</c> claim); null when anonymous, absent or not a <see cref="Guid"/>.</summary>
    Guid? SessionId { get; }

    bool IsAuthenticated { get; }
}
