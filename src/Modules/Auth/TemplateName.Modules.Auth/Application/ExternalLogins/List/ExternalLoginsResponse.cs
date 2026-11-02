namespace TemplateName.Modules.Auth.Application.ExternalLogins.List;

/// <summary>The caller's external logins: at most one for each provider, so the list is not paged.</summary>
/// <param name="HasPassword">Whether the caller has a password; with no password, the last login cannot be unlinked.</param>
/// <param name="Logins">The caller's logins in provider order, a switched-off provider's included.</param>
internal sealed record ExternalLoginsResponse(bool HasPassword, IReadOnlyList<ExternalLoginResponse> Logins)
{
    /// <summary>Only the type name: the logins carry email addresses.</summary>
    public override string ToString() => nameof(ExternalLoginsResponse);
}
