using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>
/// One attempt series to deliver a <see cref="Notification"/> through one channel. It starts <see cref="DeliveryStatus.Pending"/> and
/// ends <see cref="DeliveryStatus.Sent"/>, <see cref="DeliveryStatus.DeadLettered"/> or <see cref="DeliveryStatus.Expired"/>; only a
/// dead-lettered delivery can return to pending, through <see cref="Retry"/>. Created through <see cref="Notification.AddDelivery"/>.
/// </summary>
/// <remarks>
/// The delivery worker claims deliveries with lease-guarded SQL (D14) that counts the attempt and sets <see cref="LockedUntil"/>, then
/// settles each through the transitions below, saved only while <see cref="LockedUntil"/> still holds the lease it claimed (a concurrency
/// token). The transitions therefore never count attempts; they release the lease.
/// </remarks>
internal sealed class Delivery : Entity<Guid>
{
    /// <summary>
    /// The column limit of <see cref="Destination"/>, and the longest email address an email delivery accepts; 320 is the customary size
    /// (64 for the local part, an at sign and 255 for the domain).
    /// </summary>
    public const int MaxDestinationLength = 320;

    /// <summary>The column limit of <see cref="LastError"/>.</summary>
    public const int MaxLastErrorLength = 200;

    /// <summary>The column limit of <see cref="RenderedSubject"/>.</summary>
    public const int MaxRenderedSubjectLength = 300;

    // EF Core materializes the entity through this constructor; callers use Notification.AddDelivery.
    private Delivery()
    {
    }

    public Guid NotificationId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    /// <summary>The address the message goes to (snapshotted when the delivery was created); <see langword="null"/> for in-app.</summary>
    public string? Destination { get; private set; }

    public DeliveryStatus Status { get; private set; }

    /// <summary>The attempts started so far, counted when the worker claims the delivery; reset to 0 by <see cref="Retry"/>.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the next attempt is due (UTC); <see langword="null"/> unless <see cref="DeliveryStatus.Pending"/>.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>The end of the lease held by the worker that claimed the delivery (UTC); <see langword="null"/> when nobody holds it.</summary>
    public DateTime? LockedUntil { get; private set; }

    /// <summary>A reason code such as <c>smtp_550</c> or <c>render_failed</c>, never free text (it must not carry an address or a link).</summary>
    public string? LastError { get; private set; }

    /// <summary>The subject the recipient got, kept for the administrator's view; <see langword="null"/> until sent.</summary>
    public string? RenderedSubject { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? SentAt { get; private set; }

    public DateTime? DeadLetteredAt { get; private set; }

    /// <summary>Copied from the notification (UTC); a delivery still pending at this time is expired, not sent.</summary>
    public DateTime? ExpiresAt { get; private set; }

    internal static Delivery Create(
        Guid notificationId,
        NotificationChannel channel,
        string? destination,
        DateTimeOffset firstAttemptAt,
        DateTime? expiresAt,
        DateTimeOffset now) =>
        new()
        {
            Id = SequentialGuid.Create(now),
            NotificationId = notificationId,
            Channel = channel,
            Destination = destination,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = firstAttemptAt.UtcDateTime,
            CreatedAt = now.UtcDateTime,
            ExpiresAt = expiresAt,
        };

    /// <summary>
    /// Puts a dead-lettered delivery back in the queue, due now, with a fresh attempt budget. Refused for any other status, and for a
    /// delivery whose expiry has passed: its message would be stale.
    /// </summary>
    public Result Retry(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.DeadLettered)
        {
            return Result.Failure(DeliveryErrors.NotRetryable);
        }

        if (ExpiresAt is { } expiresAt && now.UtcDateTime >= expiresAt)
        {
            return Result.Failure(DeliveryErrors.Expired);
        }

        Status = DeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now.UtcDateTime;
        LockedUntil = null;
        LastError = null;
        DeadLetteredAt = null;

        return Result.Success();
    }

    /// <summary>Settles a pending delivery as sent. <paramref name="renderedSubject"/> is cut to <see cref="MaxRenderedSubjectLength"/>.</summary>
    /// <exception cref="InvalidOperationException">The delivery is not pending.</exception>
    public void MarkSent(string? renderedSubject, DateTimeOffset now)
    {
        EnsurePending();

        Status = DeliveryStatus.Sent;
        SentAt = now.UtcDateTime;
        RenderedSubject = BoundedText.Cut(renderedSubject, MaxRenderedSubjectLength);
        Settle();
    }

    /// <summary>
    /// Settles a pending delivery as given up. <paramref name="reason"/> is a reason code, cut to <see cref="MaxLastErrorLength"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The delivery is not pending.</exception>
    public void DeadLetter(string reason, DateTimeOffset now)
    {
        EnsurePending();

        Status = DeliveryStatus.DeadLettered;
        DeadLetteredAt = now.UtcDateTime;
        LastError = BoundedText.Cut(reason, MaxLastErrorLength);
        Settle();
    }

    /// <summary>
    /// Keeps a pending delivery pending after a transient failure: the next attempt is due at <paramref name="nextAttemptAt"/> (from
    /// <see cref="DeliveryRetryPolicy"/>), and <paramref name="reason"/>, a reason code cut to <see cref="MaxLastErrorLength"/>, is
    /// recorded. The lease is released; the attempt was already counted when the worker claimed the delivery.
    /// </summary>
    /// <exception cref="InvalidOperationException">The delivery is not pending.</exception>
    public void ScheduleRetry(string reason, DateTime nextAttemptAt)
    {
        EnsurePending();

        NextAttemptAt = nextAttemptAt;
        LastError = BoundedText.Cut(reason, MaxLastErrorLength);
        LockedUntil = null;
    }

    /// <summary>Settles a pending delivery as expired: the notification is too old to be worth sending.</summary>
    /// <exception cref="InvalidOperationException">The delivery is not pending.</exception>
    public void Expire()
    {
        EnsurePending();

        Status = DeliveryStatus.Expired;
        Settle();
    }

    private void EnsurePending()
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException($"A {Status} delivery cannot change state; only a pending one can.");
        }
    }

    private void Settle()
    {
        NextAttemptAt = null;
        LockedUntil = null;
    }
}
