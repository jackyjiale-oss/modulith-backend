using System.Globalization;
using FluentValidation;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.Me.Update;

/// <summary>
/// The display name must be set and fit its column. The language must be a predefined culture name and the time zone an IANA id that
/// this machine knows (<see cref="TimeZoneInfo.TryFindSystemTimeZoneById"/>; on Linux that needs the tz database, and both it and the
/// culture data need ICU, so a runtime image with invariant globalization would refuse every value).
/// </summary>
internal sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    // The column limit of User.TimeZone; kept equal to UserConfiguration.TimeZoneMaxLength.
    private const int MaxTimeZoneLength = 64;

    public UpdateProfileCommandValidator()
    {
        RuleFor(command => command.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxDisplayNameLength);

        RuleFor(command => command.Locale)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxLocaleLength)
            .Must(locale => TryGetCanonicalLocale(locale, out _))
            .WithMessage("'{PropertyName}' must be a known culture name, such as en, ms or zh-Hans.");

        RuleFor(command => command.TimeZone)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(MaxTimeZoneLength)
            .Must(IsIanaTimeZone)
            .WithMessage("'{PropertyName}' must be an IANA time zone id, such as Asia/Kuala_Lumpur or UTC.");
    }

    /// <summary>
    /// The canonical spelling of a predefined culture name (<c>MS-my</c> becomes <c>ms-MY</c>); false for an unknown or custom culture
    /// and for the invariant culture, which has no name.
    /// </summary>
    internal static bool TryGetCanonicalLocale(string locale, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            canonical = CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }

        return canonical.Length > 0;
    }

    // IANA ids never contain white space, while the Windows ids that Windows also resolves ("Pacific Standard Time") do: refusing them
    // keeps a value accepted on a developer's Windows machine and refused on Linux from ever being stored.
    private static bool IsIanaTimeZone(string timeZone)
    {
        if (timeZone.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
