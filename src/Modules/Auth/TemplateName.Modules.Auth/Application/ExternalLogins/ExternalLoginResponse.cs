using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.ExternalLogins;

/// <summary>One external login of the caller, as <c>GET /logins</c> lists it and <c>POST /{provider}/link</c> answers it. Never the subject.</summary>
/// <param name="Id">The login's id: what <c>DELETE /logins/{id}</c> takes.</param>
/// <param name="Provider">The provider's configured name, lower-case, as stored: a switched-off provider's login still shows it.</param>
/// <param name="Email">The email the provider gave when the login was linked, for display only; null when it gave none.</param>
/// <param name="LinkedAt">When the login was linked (UTC).</param>
/// <param name="LastUsedAt">When a sign-in last used it (UTC); null until then.</param>
internal sealed record ExternalLoginResponse(Guid Id, string Provider, string? Email, DateTimeOffset LinkedAt, DateTimeOffset? LastUsedAt)
{
    /// <summary>The response for a loaded login.</summary>
    internal static ExternalLoginResponse From(UserExternalLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);

        return new ExternalLoginResponse(login.Id, login.Provider, login.Email, login.LinkedAt, login.LastUsedAt);
    }

    /// <summary>Only the type name: the generated <c>ToString</c> would print the email.</summary>
    public override string ToString() => nameof(ExternalLoginResponse);
}
