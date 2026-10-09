using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications;

public sealed class InAppNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static InAppNotification Create(string title = "Password changed", string body = "Your password was changed.") =>
        InAppNotification.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "auth.password_changed", NotificationCategory.Security, title, body, Now);

    [Fact]
    public void Create_uses_the_delivery_id_as_its_own_id()
    {
        var deliveryId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var inApp = InAppNotification.Create(
            deliveryId, userId, notificationId, "auth.password_changed", NotificationCategory.Security, "Password changed", "Your password was changed.", Now);

        inApp.Id.ShouldBe(deliveryId);
        inApp.UserId.ShouldBe(userId);
        inApp.NotificationId.ShouldBe(notificationId);
        inApp.TypeCode.ShouldBe("auth.password_changed");
        inApp.Category.ShouldBe(NotificationCategory.Security);
        inApp.Title.ShouldBe("Password changed");
        inApp.Body.ShouldBe("Your password was changed.");
        inApp.CreatedAt.ShouldBe(Now.UtcDateTime);
        inApp.ReadAt.ShouldBeNull();
    }

    [Fact]
    public void MarkRead_is_idempotent_and_keeps_the_first_time()
    {
        var inApp = Create();

        inApp.MarkRead(Now.AddMinutes(5));
        inApp.MarkRead(Now.AddMinutes(9));

        inApp.ReadAt.ShouldBe(Now.AddMinutes(5).UtcDateTime);
    }

    [Fact]
    public void Create_cuts_a_title_and_a_body_longer_than_their_columns()
    {
        var inApp = Create(new string('t', 300), new string('b', 3000));

        inApp.Title.Length.ShouldBe(InAppNotification.MaxTitleLength);
        inApp.Body.Length.ShouldBe(InAppNotification.MaxBodyLength);
    }

    [Fact]
    public void Create_does_not_split_a_surrogate_pair_when_cutting()
    {
        // 199 letters, then an emoji (two UTF-16 units): keeping 200 units would leave half of it.
        var inApp = Create(new string('a', 199) + "\U0001F600");

        inApp.Title.ShouldBe(new string('a', 199));
    }

    [Fact]
    public void NotFound_carries_the_id()
    {
        var id = Guid.NewGuid();

        var error = InAppNotificationErrors.NotFound(id);

        error.Code.ShouldBe("notifications.notification_not_found");
        error.Type.ShouldBe(ErrorType.NotFound);
        error.Parameters.ShouldNotBeNull()["id"].ShouldBe(id);
    }
}
