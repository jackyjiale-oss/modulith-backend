using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.InApp;

/// <summary>
/// What a user sees in their inbox: the rendered title and body of an in-app delivery. Its id <b>is</b> the delivery's id, so a retried
/// delivery cannot create a second row (the key rejects it).
/// </summary>
internal sealed class InAppNotification : Entity<Guid>
{
    /// <summary>The column limit of <see cref="Title"/>.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The column limit of <see cref="Body"/>.</summary>
    public const int MaxBodyLength = 2000;

    // EF Core materializes the entity through this constructor; callers use Create.
    private InAppNotification()
    {
    }

    public Guid UserId { get; private set; }

    public Guid NotificationId { get; private set; }

    public string TypeCode { get; private set; } = string.Empty;

    public NotificationCategory Category { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    /// <summary>When the user first read it (UTC); <see langword="null"/> while unread.</summary>
    public DateTime? ReadAt { get; private set; }

    /// <summary>Creates the row for an in-app delivery. A title or body longer than its column is cut to it.</summary>
    public static InAppNotification Create(
        Guid deliveryId,
        Guid userId,
        Guid notificationId,
        string typeCode,
        NotificationCategory category,
        string title,
        string body,
        DateTimeOffset now) =>
        new()
        {
            Id = deliveryId,
            UserId = userId,
            NotificationId = notificationId,
            TypeCode = typeCode,
            Category = category,
            Title = BoundedText.Cut(title, MaxTitleLength),
            Body = BoundedText.Cut(body, MaxBodyLength),
            CreatedAt = now.UtcDateTime,
        };

    /// <summary>Marks the notification read. Idempotent: a second call keeps the first time.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now.UtcDateTime;
}
