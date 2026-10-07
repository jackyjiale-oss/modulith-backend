namespace TemplateName.Application.Common.Localization;

/// <summary>Translates error codes into messages for <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.</summary>
public interface IErrorMessageLocalizer
{
    /// <summary>
    /// Returns the message for <paramref name="code"/> from the first registered <c>*ErrorMessages</c> resource that has it, with each
    /// <c>{name}</c> placeholder replaced by <paramref name="parameters"/>[name] (unknown placeholders are left as they are), or
    /// <paramref name="fallback"/> when no resource has the code.
    /// </summary>
    string Localize(string code, IReadOnlyDictionary<string, object?>? parameters, string fallback);
}
