using System.Text.Json.Serialization;
using TemplateName.Modules.Auth.Application.Authentication.Login;

namespace TemplateName.Modules.Auth.Application.Authentication;

/// <summary>
/// The answer of a correct password (plan decision D8): <see cref="MfaRequired"/> tells the two shapes apart. <c>false</c>: the tokens of
/// a new session, as Plan 2's login answered them. <c>true</c>: the account has two-step verification, so instead of tokens a challenge
/// that <c>POST /api/v1/auth/mfa/verify</c> trades, with a code, for them. The members of the other shape are null and left out of the
/// JSON. Make one with <see cref="Signed"/> or <see cref="Challenge"/>.
/// </summary>
/// <param name="MfaRequired">Whether a second factor must follow.</param>
/// <param name="AccessToken">The ES256 JWT to send as <c>Authorization: Bearer</c>; only when signed in.</param>
/// <param name="AccessTokenExpiresAt">The access token's <c>exp</c>; only when signed in.</param>
/// <param name="RefreshToken">The secret that gets the next token pair (43 base64url characters, single use); only when signed in.</param>
/// <param name="RefreshTokenExpiresAt">When the refresh token stops working unless it is used first; only when signed in.</param>
/// <param name="SessionId">The session, also the access token's <c>sid</c> claim; only when signed in.</param>
/// <param name="ChallengeToken">The challenge (43 base64url characters), shown only here; the server keeps its hash. Only when a second factor must follow.</param>
/// <param name="ChallengeExpiresAt">When the challenge stops working (<c>Auth:Mfa:ChallengeLifetime</c> after the password); only with a challenge.</param>
/// <param name="Methods">The factors the challenge accepts: <c>totp</c> and <c>recovery_code</c>; only with a challenge.</param>
internal sealed record LoginResult(
    bool MfaRequired,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccessToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RefreshTokenExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChallengeToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ChallengeExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Methods)
{
    /// <summary>The factors a challenge accepts, in the order a client should offer them.</summary>
    private static readonly IReadOnlyList<string> ChallengeMethods = ["totp", "recovery_code"];

    /// <summary>A sign-in that is complete: the tokens of the new session.</summary>
    public static LoginResult Signed(LoginResponse tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        return new LoginResult(
            MfaRequired: false,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAt,
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAt,
            tokens.SessionId,
            ChallengeToken: null,
            ChallengeExpiresAt: null,
            Methods: null);
    }

    /// <summary>A sign-in that needs a second factor: the challenge and when it expires.</summary>
    public static LoginResult Challenge(string challengeToken, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeToken);

        return new LoginResult(
            MfaRequired: true,
            AccessToken: null,
            AccessTokenExpiresAt: null,
            RefreshToken: null,
            RefreshTokenExpiresAt: null,
            SessionId: null,
            challengeToken,
            expiresAt,
            ChallengeMethods);
    }

    /// <summary>Only the type name and the shape, so a logged result can never carry a token or the challenge.</summary>
    public override string ToString() => $"{nameof(LoginResult)} {{ {nameof(MfaRequired)} = {MfaRequired} }}";
}
