using PhoneNumbers;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Notifications.Domain.Deliveries;

namespace TemplateName.UnitTests.Notifications.Sms;

/// <summary>
/// Auth decides which numbers an account may verify (libphonenumber) and Notifications which destinations it texts (<see cref="SmsDestination"/>).
/// A number Auth accepts but Notifications refuses would answer 202, spend one of the day's codes and never be texted.
/// </summary>
public sealed class PhoneNumberAgreementTests
{
    private static readonly IReadOnlySet<string> AnyRegion = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void Every_number_auth_accepts_can_be_texted()
    {
        var util = PhoneNumberUtil.GetInstance();
        var refused = new List<string>();

        foreach (var region in util.GetSupportedRegions().Order(StringComparer.Ordinal))
        {
            if (util.GetExampleNumberForType(region, PhoneNumberType.MOBILE) is not { } example)
            {
                continue;
            }

            var international = util.Format(example, PhoneNumberFormat.INTERNATIONAL);
            if (PhoneNumberRules.TryNormalize(international, AnyRegion, out var e164) && !SmsDestination.IsValid(e164))
            {
                refused.Add($"{region} {e164.Length - 1} digits");
            }
        }

        refused.ShouldBeEmpty();
    }
}
