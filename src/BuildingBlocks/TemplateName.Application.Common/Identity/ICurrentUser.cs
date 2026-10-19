namespace TemplateName.Application.Common.Identity;

/// <summary>The user making the current request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    /// <summary>The session the caller's access token belongs to (the <c>sid</c> claim); null when anonymous, absent or not a <see cref="Guid"/>.</summary>
    Guid? SessionId { get; }

    bool IsAuthenticated { get; }

    /// <summary>
    /// When the user signed in (the <c>auth_time</c> claim, Unix seconds), not when the access token was issued; null when anonymous,
    /// absent or not a whole number. It comes from the signed token, so a step-up decision needs no database read.
    /// </summary>
    DateTimeOffset? AuthTime { get; }

    /// <summary>
    /// How the user signed in: every <c>amr</c> claim value, in token order (for example <c>pwd</c>, <c>otp</c>, <c>mfa</c>); empty when
    /// anonymous or absent. It comes from the signed token, so a step-up decision needs no database read.
    /// </summary>
    IReadOnlyCollection<string> AuthMethods { get; }
}
