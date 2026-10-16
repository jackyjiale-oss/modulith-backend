using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// D15: the SMTP settings moved from <c>Auth:Email</c> to <see cref="EmailOptions.SectionName"/>. Validated on start with
/// <see cref="EmailOptions"/>, this check fails while any key under <c>Auth:Email</c> is still configured, from any source (a settings
/// file, user secrets, an environment variable such as <c>Auth__Email__Host</c>): silently ignoring it would send production mail to
/// the default <c>localhost</c>. The message names the new section only, never a configured value.
/// </summary>
internal sealed class LegacyEmailSettingsCheck(IConfiguration configuration) : IValidateOptions<EmailOptions>
{
    internal const string LegacySectionName = "Auth:Email";

    internal const string Message = "Auth:Email has moved to Notifications:Email (see docs/modules/notifications.md#configuration).";

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
        => configuration.GetSection(LegacySectionName).Exists() ? ValidateOptionsResult.Fail(Message) : ValidateOptionsResult.Success;
}
