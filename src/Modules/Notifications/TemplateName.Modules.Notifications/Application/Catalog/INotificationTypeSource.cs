namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// Declares notification types. Every registered source is read by <see cref="NotificationCatalog"/>; the templates of a type are
/// embedded in the assembly of the source that declares it.
/// </summary>
internal interface INotificationTypeSource
{
    IReadOnlyCollection<NotificationTypeDefinition> Types { get; }
}
