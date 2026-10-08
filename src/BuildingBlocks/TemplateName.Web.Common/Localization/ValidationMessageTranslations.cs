using FluentValidation.Resources;

namespace TemplateName.Web.Common.Localization;

/// <summary>
/// FluentValidation's built-in messages plus <c>ms</c> and <c>zh-Hans</c> translations, which it does not ship (it has <c>zh-CN</c>,
/// which a <c>zh-Hans</c> UI culture never reaches). Covers the validators the solution uses and the other common ones; add a row
/// when a validator not listed here starts being used. Placeholders (<c>{PropertyName}</c>, …) stay exactly as in the English text.
/// The translations are drafts that need native review.
/// </summary>
internal sealed class ValidationMessageTranslations : LanguageManager
{
    private static readonly (string Key, string Malay, string SimplifiedChinese)[] Translations =
    [
        ("NotNullValidator",
            "'{PropertyName}' tidak boleh kosong.",
            "'{PropertyName}' 不能为空。"),
        ("NotEmptyValidator",
            "'{PropertyName}' tidak boleh kosong.",
            "'{PropertyName}' 不能为空。"),
        ("LengthValidator",
            "'{PropertyName}' mestilah antara {MinLength} hingga {MaxLength} aksara. Anda memasukkan {TotalLength} aksara.",
            "'{PropertyName}' 的长度必须在 {MinLength} 到 {MaxLength} 个字符之间。您输入了 {TotalLength} 个字符。"),
        ("MinimumLengthValidator",
            "Panjang '{PropertyName}' mestilah sekurang-kurangnya {MinLength} aksara. Anda memasukkan {TotalLength} aksara.",
            "'{PropertyName}' 的长度不能少于 {MinLength} 个字符。您输入了 {TotalLength} 个字符。"),
        ("MaximumLengthValidator",
            "Panjang '{PropertyName}' tidak boleh melebihi {MaxLength} aksara. Anda memasukkan {TotalLength} aksara.",
            "'{PropertyName}' 的长度不能超过 {MaxLength} 个字符。您输入了 {TotalLength} 个字符。"),
        ("GreaterThanValidator",
            "'{PropertyName}' mestilah lebih besar daripada '{ComparisonValue}'.",
            "'{PropertyName}' 必须大于 '{ComparisonValue}'。"),
        ("GreaterThanOrEqualValidator",
            "'{PropertyName}' mestilah lebih besar daripada atau sama dengan '{ComparisonValue}'.",
            "'{PropertyName}' 必须大于或等于 '{ComparisonValue}'。"),
        ("LessThanValidator",
            "'{PropertyName}' mestilah kurang daripada '{ComparisonValue}'.",
            "'{PropertyName}' 必须小于 '{ComparisonValue}'。"),
        ("LessThanOrEqualValidator",
            "'{PropertyName}' mestilah kurang daripada atau sama dengan '{ComparisonValue}'.",
            "'{PropertyName}' 必须小于或等于 '{ComparisonValue}'。"),
        ("InclusiveBetweenValidator",
            "'{PropertyName}' mestilah antara {From} hingga {To}. Anda memasukkan {PropertyValue}.",
            "'{PropertyName}' 必须介于 {From} 和 {To} 之间。您输入了 {PropertyValue}。"),
        ("EmailValidator",
            "'{PropertyName}' bukan alamat e-mel yang sah.",
            "'{PropertyName}' 不是有效的电子邮件地址。"),
        ("EnumValidator",
            "Julat nilai '{PropertyName}' tidak termasuk '{PropertyValue}'.",
            "'{PropertyName}' 的取值范围不包含 '{PropertyValue}'。"),
    ];

    public ValidationMessageTranslations()
    {
        foreach (var (key, malay, simplifiedChinese) in Translations)
        {
            AddTranslation("ms", key, malay);
            AddTranslation("zh-Hans", key, simplifiedChinese);
        }
    }

    /// <summary>The FluentValidation message keys translated here.</summary>
    internal static IEnumerable<string> TranslatedKeys => Translations.Select(translation => translation.Key);
}
