using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications;

public sealed class DeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static Delivery NewDelivery(DateTimeOffset? expiresAt = null)
    {
        var notification = Notification.Create(
            "auth.password_reset_requested", NotificationPriority.Critical, Guid.NewGuid(), "en", "{}", null, Guid.NewGuid(), null, expiresAt, Now);

        return notification.AddDelivery(NotificationChannel.Email, "alice@example.com", Now, Now);
    }

    private static Delivery DeadLettered(DateTimeOffset? expiresAt = null, string reason = "smtp_550")
    {
        var delivery = NewDelivery(expiresAt);
        delivery.DeadLetter(reason, Now.AddMinutes(1));

        return delivery;
    }

    [Fact]
    public void Retry_only_from_dead_lettered_and_not_after_expiry()
    {
        // Pending, Sent and Expired deliveries cannot be retried.
        NewDelivery().Retry(Now).Error.ShouldBe(DeliveryErrors.NotRetryable);

        var sent = NewDelivery();
        sent.MarkSent("Subject", Now);
        sent.Retry(Now).Error.ShouldBe(DeliveryErrors.NotRetryable);

        var expired = NewDelivery(Now.AddMinutes(30));
        expired.Expire();
        expired.Retry(Now.AddMinutes(32)).Error.ShouldBe(DeliveryErrors.NotRetryable);

        // A dead-lettered delivery at or past its expiry is refused; one before it is reset.
        var late = DeadLettered(Now.AddMinutes(30));
        var refused = late.Retry(Now.AddMinutes(30));
        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(DeliveryErrors.Expired);
        late.Status.ShouldBe(DeliveryStatus.DeadLettered);

        var retryAt = Now.AddMinutes(29);
        var delivery = DeadLettered(Now.AddMinutes(30));
        delivery.Retry(retryAt).IsSuccess.ShouldBeTrue();

        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);
        delivery.NextAttemptAt.ShouldBe(retryAt.UtcDateTime);
        delivery.LastError.ShouldBeNull();
        delivery.DeadLetteredAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public void Retry_without_an_expiry_resets_the_delivery_and_it_can_dead_letter_again()
    {
        var delivery = DeadLettered(expiresAt: null);

        delivery.Retry(Now.AddDays(3)).IsSuccess.ShouldBeTrue();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);

        delivery.DeadLetter("render_failed", Now.AddDays(3).AddMinutes(1));
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("render_failed");
    }

    [Fact]
    public void Error_codes_and_types_are_the_documented_ones()
    {
        DeliveryErrors.NotRetryable.Code.ShouldBe("notifications.delivery_not_retryable");
        DeliveryErrors.NotRetryable.Type.ShouldBe(ErrorType.Conflict);
        DeliveryErrors.Expired.Code.ShouldBe("notifications.delivery_expired");
        DeliveryErrors.Expired.Type.ShouldBe(ErrorType.Conflict);

        var id = Guid.NewGuid();
        var notFound = DeliveryErrors.NotFound(id);
        notFound.Code.ShouldBe("notifications.delivery_not_found");
        notFound.Type.ShouldBe(ErrorType.NotFound);
        notFound.Parameters.ShouldNotBeNull()["id"].ShouldBe(id);
    }

    [Fact]
    public void DeadLetter_records_the_reason_code_and_clears_the_schedule()
    {
        var delivery = NewDelivery();

        delivery.DeadLetter("smtp_550", Now.AddMinutes(5));

        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("smtp_550");
        delivery.DeadLetteredAt.ShouldBe(Now.AddMinutes(5).UtcDateTime);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public void DeadLetter_cuts_a_reason_longer_than_the_column()
    {
        var delivery = NewDelivery();

        delivery.DeadLetter(new string('x', 500), Now);

        delivery.LastError!.Length.ShouldBe(Delivery.MaxLastErrorLength);
    }

    [Fact]
    public void MarkSent_stores_the_subject_cut_to_its_column_and_clears_the_schedule()
    {
        var delivery = NewDelivery();

        delivery.MarkSent(new string('s', 400), Now.AddSeconds(3));

        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(Now.AddSeconds(3).UtcDateTime);
        delivery.RenderedSubject!.Length.ShouldBe(Delivery.MaxRenderedSubjectLength);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public void Expire_clears_the_schedule_and_only_works_from_pending()
    {
        var delivery = NewDelivery(Now.AddMinutes(30));

        delivery.Expire();

        delivery.Status.ShouldBe(DeliveryStatus.Expired);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => delivery.Expire());
    }

    [Fact]
    public void Transitions_out_of_pending_are_refused_from_other_states()
    {
        var sent = NewDelivery();
        sent.MarkSent("Subject", Now);

        Should.Throw<InvalidOperationException>(() => sent.DeadLetter("smtp_550", Now));
        Should.Throw<InvalidOperationException>(() => sent.MarkSent("Subject", Now));
    }
}
