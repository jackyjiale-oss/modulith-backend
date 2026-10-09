using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Notifications;

/// <summary>
/// One message to one recipient, created when an integration event is consumed. It snapshots everything rendering needs (the culture
/// and the variables), so a retry never depends on the sender. Its <see cref="Deliveries"/> are one per channel. Secrets never rest
/// here in the clear: <see cref="ProtectedData"/> holds ciphertext produced by the publisher, stored as given and never decrypted by
/// the domain.
/// </summary>
internal sealed class Notification : AggregateRoot<Guid>
{
    /// <summary>The column limit of <see cref="CorrelationId"/> (a W3C trace id is 32 characters).</summary>
    public const int MaxCorrelationIdLength = 32;

    private readonly List<Delivery> _deliveries = [];

    // EF Core materializes the aggregate through this constructor; callers use Create.
    private Notification()
    {
    }

    /// <summary>The notification type code from the catalog, for example <c>auth.password_changed</c>.</summary>
    public string TypeCode { get; private set; } = string.Empty;

    public Guid RecipientUserId { get; private set; }

    public NotificationPriority Priority { get; private set; }

    /// <summary>The supported culture (<c>en</c>, <c>ms</c>, <c>zh-Hans</c>) the message is rendered in, resolved once at creation.</summary>
    public string Culture { get; private set; } = string.Empty;

    /// <summary>A JSON object of the non-secret template variables.</summary>
    public string Data { get; private set; } = string.Empty;

    /// <summary>A JSON object of secret variable name to <c>ISecretProtector</c> ciphertext (a single-use link); <see langword="null"/> when there is none.</summary>
    public string? ProtectedData { get; private set; }

    /// <summary>The trace id of the request or job that caused the notification, for support.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>The id of the integration event this notification was created for; with the type and recipient it makes consumption idempotent.</summary>
    public Guid SourceMessageId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>After this time (UTC) the notification is stale and its deliveries are expired instead of sent; <see langword="null"/> never expires.</summary>
    public DateTime? ExpiresAt { get; private set; }

    public IReadOnlyCollection<Delivery> Deliveries => _deliveries.AsReadOnly();

    /// <summary>Creates a notification without deliveries. <paramref name="data"/> and <paramref name="protectedData"/> are stored as given.</summary>
    /// <exception cref="ArgumentException">A blank type code, culture or data.</exception>
    public static Notification Create(
        string typeCode,
        NotificationPriority priority,
        Guid recipientUserId,
        string culture,
        string data,
        string? protectedData,
        Guid sourceMessageId,
        string? correlationId,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        return new Notification
        {
            Id = SequentialGuid.Create(now),
            TypeCode = typeCode,
            RecipientUserId = recipientUserId,
            Priority = priority,
            Culture = culture,
            Data = data,
            ProtectedData = protectedData,
            CorrelationId = BoundedText.Cut(correlationId, MaxCorrelationIdLength),
            SourceMessageId = sourceMessageId,
            CreatedAt = now.UtcDateTime,
            ExpiresAt = expiresAt?.UtcDateTime,
        };
    }

    /// <summary>
    /// Adds the delivery for <paramref name="channel"/>, first due at <paramref name="firstAttemptAt"/> (later than <paramref name="now"/>
    /// when quiet hours defer it). It inherits the notification's expiry. An email delivery needs one plain address as
    /// <paramref name="destination"/> (<see cref="EmailDestination"/>).
    /// </summary>
    /// <exception cref="ArgumentException">An email delivery without a single valid address; the message does not quote it.</exception>
    /// <exception cref="InvalidOperationException">The notification already has a delivery for the channel.</exception>
    public Delivery AddDelivery(NotificationChannel channel, string? destination, DateTimeOffset firstAttemptAt, DateTimeOffset now)
    {
        if (channel == NotificationChannel.Email && !EmailDestination.IsValid(destination))
        {
            throw new ArgumentException("An email delivery needs exactly one valid email address.", nameof(destination));
        }

        if (_deliveries.Exists(delivery => delivery.Channel == channel))
        {
            throw new InvalidOperationException($"The notification already has a {channel} delivery.");
        }

        var delivery = Delivery.Create(Id, channel, destination, firstAttemptAt, ExpiresAt, now);
        _deliveries.Add(delivery);

        return delivery;
    }
}
