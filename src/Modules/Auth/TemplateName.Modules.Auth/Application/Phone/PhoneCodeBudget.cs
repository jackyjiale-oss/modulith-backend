using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Application.Phone;

/// <summary>Why the budget refused a code, and when the next one is allowed: the reason is <c>cooldown</c> or <c>daily_cap</c>.</summary>
internal sealed record PhoneCodeBudgetRefusal(TimeSpan RetryAfter, string Reason);

/// <summary>
/// One SMS budget per user, shared by both purposes of a phone code: at most one code per <see cref="PhoneOptions.ResendCooldown"/> and
/// <see cref="PhoneOptions.MaxCodesPerDay"/> in any 24 hours. The issuing save sets <c>User.PhoneCodeRequestedAt</c>, so two
/// simultaneous requests cannot both pass it.
/// </summary>
internal sealed class PhoneCodeBudget(IPhoneVerificationCodeRepository codes)
{
    internal const string Cooldown = "cooldown";
    internal const string DailyCap = "daily_cap";

    private static readonly TimeSpan DailyWindow = TimeSpan.FromDays(1);

    /// <summary>Null when the user may be sent a code now; otherwise why not and for how long.</summary>
    public async Task<PhoneCodeBudgetRefusal?> CheckAsync(Guid userId, PhoneOptions settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (await codes.GetLastIssuedAtAsync(userId, cancellationToken) is { } lastIssuedAt && now - lastIssuedAt < settings.ResendCooldown)
        {
            return new PhoneCodeBudgetRefusal(lastIssuedAt + settings.ResendCooldown - now, Cooldown);
        }

        var issuedToday = await codes.ListIssuedSinceAsync(userId, now - DailyWindow, cancellationToken);
        if (issuedToday.Count >= settings.MaxCodesPerDay)
        {
            // The next code is allowed when enough of the oldest ones have left the window to bring the count below the cap.
            return new PhoneCodeBudgetRefusal(issuedToday[issuedToday.Count - settings.MaxCodesPerDay] + DailyWindow - now, DailyCap);
        }

        return null;
    }
}
