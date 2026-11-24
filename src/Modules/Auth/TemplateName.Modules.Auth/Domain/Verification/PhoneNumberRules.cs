using PhoneNumbers;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>
/// Which phone numbers an account may hold: a valid mobile number written in international form (it starts with <c>+</c>, so no default
/// region is guessed), from an allowed region. A number is stored and compared in E.164 (<c>+60123456789</c>), so the same number typed
/// with spaces, dashes or brackets is the same number. Wraps libphonenumber.
/// </summary>
internal static class PhoneNumberRules
{
    // Longer input is refused before parsing; the request validator already stops anything over 32 characters.
    private const int MaxInputLength = 64;

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>
    /// Parses <paramref name="input"/> and returns its E.164 form when it is a valid mobile number (or a number the metadata cannot tell
    /// from one) whose region is in <paramref name="allowedRegions"/>, or any region when that set is empty.
    /// </summary>
    public static bool TryNormalize(string input, IReadOnlySet<string> allowedRegions, out string e164)
    {
        ArgumentNullException.ThrowIfNull(allowedRegions);
        e164 = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxInputLength)
        {
            return false;
        }

        PhoneNumber number;
        try
        {
            number = Util.Parse(input.Trim(), null);
        }
        catch (NumberParseException)
        {
            return false;
        }

        if (!Util.IsValidNumber(number))
        {
            return false;
        }

        var type = Util.GetNumberType(number);
        if (type is not (PhoneNumberType.MOBILE or PhoneNumberType.FIXED_LINE_OR_MOBILE))
        {
            return false;
        }

        if (allowedRegions.Count > 0 && !allowedRegions.Contains(Util.GetRegionCodeForNumber(number)))
        {
            return false;
        }

        e164 = Util.Format(number, PhoneNumberFormat.E164);
        return true;
    }

    /// <summary>Whether <paramref name="region"/> is an ISO 3166 region code (upper case) the number metadata knows.</summary>
    public static bool IsKnownRegion(string region) => Util.GetSupportedRegions().Contains(region);
}
