using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>The social login errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class ExternalLoginErrors
{
    /// <summary>One answer for a provider that is unknown and one that is disabled, so the response does not tell which.</summary>
    public static readonly Error ProviderNotFound = Error.NotFound(
        "auth.external_provider_not_found",
        "This sign-in provider is not available.");

    /// <summary>One answer for every way the proof (token, nonce, audience, issuer, age, replay) can fail, so the response does not tell which.</summary>
    public static readonly Error InvalidToken = Error.Unauthorized(
        "auth.invalid_external_token",
        "The sign-in with the provider could not be verified. Try again.");

    /// <summary>The provider's metadata or Graph call cannot be reached. Never reported as a bad token: the proof was not judged.</summary>
    public static readonly Error ProviderUnavailable = Error.Unavailable(
        "auth.external_provider_unavailable",
        "The sign-in provider cannot be reached right now. Try again later or sign in another way.");

    public static readonly Error EmailUnverified = Error.Forbidden(
        "auth.external_email_unverified",
        "The provider did not give a verified email address, so an account cannot be created or linked.");

    /// <summary>An account with that email exists and cannot be linked automatically; sent only to a caller who proved a usable email.</summary>
    public static readonly Error LinkRequired = Error.Conflict(
        "auth.external_link_required",
        "An account with this email address already exists. Sign in the usual way and link the provider from your settings.");

    public static readonly Error SignUpDisabled = Error.Forbidden(
        "auth.external_sign_up_disabled",
        "There is no account for this sign-in and new accounts cannot be created with it.");

    /// <summary>Link: the identity at the provider already belongs to another user.</summary>
    public static readonly Error IdentityTaken = Error.Conflict(
        "auth.external_identity_taken",
        "This provider account is already linked to another user.");

    /// <summary>Link: the caller already has a login for that provider, with a different identity.</summary>
    public static readonly Error ProviderAlreadyLinked = Error.Conflict(
        "auth.external_provider_already_linked",
        "You have already linked an account from this provider. Unlink it first to link another.");

    /// <summary>Unlink: one answer for an unknown id and for another user's, so the response does not tell which.</summary>
    public static readonly Error LoginNotFound = Error.NotFound(
        "auth.external_login_not_found",
        "The linked sign-in was not found.");

    /// <summary>Unlink would leave the user with neither a password nor another login.</summary>
    public static readonly Error LastLoginMethod = Error.Conflict(
        "auth.last_login_method",
        "You cannot remove your only way to sign in. Set a password or link another provider first.");

    /// <summary>Step-up by proof: the identity is not one of the caller's linked logins.</summary>
    public static readonly Error StepUpIdentityMismatch = Error.Validation(
        "auth.step_up_identity_mismatch",
        "This provider account is not linked to your user.");
}
