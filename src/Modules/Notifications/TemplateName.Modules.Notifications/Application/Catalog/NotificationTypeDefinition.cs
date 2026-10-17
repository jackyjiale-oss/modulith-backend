using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// One kind of notification, declared in code by an <see cref="INotificationTypeSource"/>. <see cref="Code"/> is <c>{area}.{name}</c>
/// (for example <c>auth.password_changed</c>). <see cref="Variables"/> are the placeholders its templates may use and
/// <see cref="SecretVariables"/> the ones that carry a secret (a single-use link): they are never shown in a subject or an in-app text, and
/// the scheduler refuses an event whose secret value does not decrypt to an absolute <c>http</c> or <c>https</c> URL.
/// </summary>
internal sealed record NotificationTypeDefinition(
    string Code,
    NotificationCategory Category,
    NotificationPriority Priority,
    IReadOnlyList<NotificationChannel> DefaultChannels,
    IReadOnlyList<NotificationChannel> MandatoryChannels,
    IReadOnlySet<string> Variables,
    IReadOnlySet<string> SecretVariables)
{
    /// <summary>True when the recipient cannot switch <paramref name="channel"/> off for this type.</summary>
    public bool IsMandatory(NotificationChannel channel) => MandatoryChannels.Contains(channel);

    /// <summary>True when the recipient can switch at least one of the default channels off.</summary>
    public bool IsConfigurable => DefaultChannels.Any(channel => !IsMandatory(channel));
}
