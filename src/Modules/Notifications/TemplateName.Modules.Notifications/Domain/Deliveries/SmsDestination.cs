using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>
/// The rule for the destination of an SMS delivery: one number in E.164, <c>+</c> and 7 to 15 digits (Tokelau's mobiles have 7), the
/// first not 0 (<c>+60123456789</c>). No spaces, dashes or other text, so nothing but one recipient can reach the provider. It accepts every
/// number Auth's phone rules accept (<c>PhoneNumberAgreementTests</c>).
/// </summary>
internal static partial class SmsDestination
{
    /// <summary>Whether <paramref name="destination"/> is one number in E.164.</summary>
    public static bool IsValid([NotNullWhen(true)] string? destination) => destination is not null && E164().IsMatch(destination);

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}\z")]
    private static partial Regex E164();
}
