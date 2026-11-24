using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>The phone number errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class PhoneErrors
{
    /// <summary>Not a valid mobile number in international form, or from a region the deployment does not allow.</summary>
    public static readonly Error InvalidPhoneNumber = Error.Validation(
        "auth.invalid_phone_number",
        "The phone number is not a valid mobile number in a supported region.");

    /// <summary>One answer for a code that is wrong, expired, used, replaced or ended by too many tries, so the cause stays private.</summary>
    public static readonly Error InvalidCode = Error.Validation(
        "auth.invalid_code",
        "The code is invalid or has expired. Request a new one.");

    /// <summary>Another account verified the number first.</summary>
    public static readonly Error PhoneNumberTaken = Error.Conflict(
        "auth.phone_number_taken",
        "This phone number is already used by another account.");
}
