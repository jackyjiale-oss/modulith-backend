namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>What a <see cref="PhoneVerificationCode"/> is for. Stored as <c>tinyint</c>; the values never change.</summary>
internal enum PhoneCodePurpose : byte
{
    /// <summary>Proves a number the user wants on the account (the <c>me/phone</c> routes).</summary>
    Verify = 1,

    /// <summary>Signs the holder of the account's verified number in (the <c>phone/sign-in</c> routes).</summary>
    SignIn = 2,
}
