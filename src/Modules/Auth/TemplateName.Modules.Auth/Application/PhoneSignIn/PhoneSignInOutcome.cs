namespace TemplateName.Modules.Auth.Application.PhoneSignIn;

/// <summary>The outcome of a phone sign-in request, the <c>outcome</c> tag of <c>auth.phone_sign_ins</c>.</summary>
internal enum PhoneSignInOutcome
{
    /// <summary>A code was issued for a known number.</summary>
    Sent,

    /// <summary>A send that texted nothing: unknown number, an account that may not sign in, the budget, or a lost race.</summary>
    Suppressed,

    /// <summary>A verify started a session.</summary>
    SignedIn,

    /// <summary>A verify answered a two-step verification challenge.</summary>
    Challenged,

    /// <summary>A verify refused the code or the account.</summary>
    Refused,
}
