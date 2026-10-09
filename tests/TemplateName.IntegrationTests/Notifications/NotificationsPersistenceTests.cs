using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Inbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using NotificationsInbox = TemplateName.Infrastructure.Common.Inbox.Inbox<TemplateName.Modules.Notifications.Infrastructure.Persistence.NotificationsDbContext>;

namespace TemplateName.IntegrationTests.Notifications;

public sealed class NotificationsPersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Consumer = "TemplateName.Tests.NotificationsConsumer";
    private const string TypeCode = "auth.password_changed";
    private const int SqlUniqueIndexViolation = 2601;

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task Notification_with_deliveries_round_trips()
    {
        var recipient = Guid.NewGuid();
        var source = Guid.NewGuid();
        var notification = Notification.Create(
            TypeCode,
            NotificationPriority.Critical,
            recipient,
            "zh-Hans",
            """{"displayName":"Alice"}""",
            """{"actionUrl":"ciphertext"}""",
            source,
            "4bf92f3577b34da6a3ce929d0e0e4736",
            Now.AddHours(24),
            Now);
        var email = notification.AddDelivery(NotificationChannel.Email, "alice@example.com", Now, Now);
        var inApp = notification.AddDelivery(NotificationChannel.InApp, null, Now, Now);

        var scope = NewScope();
        scope.GetRequiredService<INotificationRepository>().Add(notification);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        var read = NewScope();
        var saved = await read.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .SingleAsync(candidate => candidate.Id == notification.Id, Ct);
        saved.TypeCode.ShouldBe(TypeCode);
        saved.RecipientUserId.ShouldBe(recipient);
        saved.Priority.ShouldBe(NotificationPriority.Critical);
        saved.Culture.ShouldBe("zh-Hans");
        saved.Data.ShouldBe("""{"displayName":"Alice"}""");
        saved.ProtectedData.ShouldBe("""{"actionUrl":"ciphertext"}""");
        saved.CorrelationId.ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        saved.SourceMessageId.ShouldBe(source);
        saved.CreatedAt.ShouldBe(Now.UtcDateTime);
        saved.ExpiresAt.ShouldBe(Now.AddHours(24).UtcDateTime);
        saved.Deliveries.Count.ShouldBe(2);

        var savedEmail = saved.Deliveries.Single(delivery => delivery.Channel == NotificationChannel.Email);
        savedEmail.Id.ShouldBe(email.Id);
        savedEmail.NotificationId.ShouldBe(notification.Id);
        savedEmail.Destination.ShouldBe("alice@example.com");
        savedEmail.Status.ShouldBe(DeliveryStatus.Pending);
        savedEmail.AttemptCount.ShouldBe(0);
        savedEmail.NextAttemptAt.ShouldBe(Now.UtcDateTime);
        savedEmail.LockedUntil.ShouldBeNull();
        savedEmail.LastError.ShouldBeNull();
        savedEmail.RenderedSubject.ShouldBeNull();
        savedEmail.ExpiresAt.ShouldBe(Now.AddHours(24).UtcDateTime);
        saved.Deliveries.Single(delivery => delivery.Channel == NotificationChannel.InApp).Destination.ShouldBeNull();

        // The delivery comes back tracked, with its notification (and the sibling delivery) in the same context.
        var deliveries = NewScope();
        var delivery = (await deliveries.GetRequiredService<INotificationRepository>().GetDeliveryAsync(inApp.Id, Ct)).ShouldNotBeNull();
        var tracker = deliveries.GetRequiredService<NotificationsDbContext>().ChangeTracker;
        tracker.Entries<Delivery>().ShouldContain(entry => entry.Entity == delivery);
        tracker.Entries<Notification>().Single().Entity.Id.ShouldBe(notification.Id);
        (await deliveries.GetRequiredService<INotificationRepository>().GetDeliveryAsync(Guid.NewGuid(), Ct)).ShouldBeNull();

        // A tracked change saves through the unit of work.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        delivery.MarkSent("Subject", Now);
        await deliveries.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        var sent = await NewScope().GetRequiredService<NotificationsDbContext>().Set<Delivery>().SingleAsync(candidate => candidate.Id == inApp.Id, Ct);
        sent.Status.ShouldBe(DeliveryStatus.Sent);
        sent.RenderedSubject.ShouldBe("Subject");
        sent.NextAttemptAt.ShouldBeNull();
    }

    [Fact]
    public async Task DateTime_values_round_trip_as_utc()
    {
        // Every DateTime and DateTimeOffset column is datetime2(3): written as UTC and read back as UTC, not Unspecified.
        Factory.Time.Advance(TimeSpan.FromMilliseconds(1234));
        var userId = Guid.NewGuid();
        var notification = NewNotification(userId, expiresAt: Now.AddHours(1));
        var emailDelivery = notification.AddDelivery(NotificationChannel.Email, "a@example.com", Now.AddMinutes(5), Now);
        var inApp = notification.AddDelivery(NotificationChannel.InApp, null, Now, Now);
        emailDelivery.MarkSent("Subject", Now.AddSeconds(2));
        var inAppRow = InAppNotification.Create(inApp.Id, userId, notification.Id, TypeCode, NotificationCategory.Security, "Title", "Body", Now);
        inAppRow.MarkRead(Now.AddSeconds(3));
        var profile = UserNotificationProfile.Create(userId, Now);
        var preference = UserPreference.Create(userId, TypeCode, NotificationChannel.InApp, isEnabled: false, Now);
        var ticket = HubTicket.Issue(userId, Hash(1), TimeSpan.FromSeconds(30), Now);
        await SaveAsync(notification, inAppRow, profile, preference, ticket);

        var context = NewScope().GetRequiredService<NotificationsDbContext>();
        var savedNotification = await context.Set<Notification>().Include(candidate => candidate.Deliveries).SingleAsync(Ct);
        var savedInApp = await context.Set<InAppNotification>().SingleAsync(Ct);
        var savedProfile = await context.Set<UserNotificationProfile>().SingleAsync(Ct);
        var savedPreference = await context.Set<UserPreference>().SingleAsync(Ct);
        var savedTicket = await context.Set<HubTicket>().SingleAsync(Ct);
        var savedEmail = savedNotification.Deliveries.Single(delivery => delivery.Channel == NotificationChannel.Email);
        var pending = savedNotification.Deliveries.Single(delivery => delivery.Channel == NotificationChannel.InApp);

        savedNotification.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        savedNotification.CreatedAt.ShouldBe(Now.UtcDateTime);
        savedNotification.ExpiresAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        savedNotification.ExpiresAt.ShouldBe(Now.AddHours(1).UtcDateTime);
        savedEmail.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        savedEmail.SentAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        savedEmail.SentAt.ShouldBe(Now.AddSeconds(2).UtcDateTime);
        savedEmail.ExpiresAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        pending.NextAttemptAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        pending.NextAttemptAt.ShouldBe(Now.UtcDateTime);
        savedInApp.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        savedInApp.ReadAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        savedInApp.ReadAt.ShouldBe(Now.AddSeconds(3).UtcDateTime);
        savedProfile.UpdatedAt.ShouldBe(Now);
        savedProfile.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero);
        savedPreference.UpdatedAt.ShouldBe(Now);
        savedPreference.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero);
        savedTicket.CreatedAt.ShouldBe(Now);
        savedTicket.ExpiresAt.ShouldBe(Now.AddSeconds(30));
        savedTicket.ExpiresAt.Offset.ShouldBe(TimeSpan.Zero);
        savedTicket.ConsumedAt.ShouldBeNull();

        // The column type itself: datetime2 with precision 3.
        foreach (var (table, column) in new[] { ("Notifications", "CreatedAt"), ("Deliveries", "SentAt"), ("InAppNotifications", "ReadAt"), ("UserSettings", "UpdatedAt"), ("HubTickets", "ExpiresAt") })
        {
            (await ColumnTypeAsync(context, table, column)).ShouldBe("datetime2(3)", $"{table}.{column}");
        }
    }

    [Fact]
    public async Task Duplicate_source_message_type_and_recipient_violates_the_unique_index()
    {
        var recipient = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SaveAsync(NewNotification(recipient, source));

        // Another recipient, another type or another source message is a different notification.
        await SaveAsync(
            NewNotification(Guid.NewGuid(), source),
            NewNotification(recipient, source, "auth.user_locked_out"),
            NewNotification(recipient, Guid.NewGuid()));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(NewNotification(recipient, source)));

        var sql = exception.InnerException.ShouldBeOfType<SqlException>();
        sql.Number.ShouldBe(SqlUniqueIndexViolation);
        sql.Message.ShouldContain("IX_Notifications_SourceMessageId_TypeCode_RecipientUserId");
        (await NewScope().GetRequiredService<NotificationsDbContext>().Set<Notification>().CountAsync(Ct)).ShouldBe(4);
    }

    [Fact]
    public async Task Inbox_duplicate_save_returns_false_and_writes_nothing()
    {
        var messageId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var first = NewScope();
        var second = NewScope();

        // Both consumers checked before either saved, so both go ahead and race to save the same message: the same inbox record and
        // the same notification (source message, type and recipient). EF inserts the inbox row first, so the loser waits on the winner's
        // inbox key and fails on that, never on the notification index.
        (await first.GetRequiredService<IInbox>().HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeFalse();
        (await second.GetRequiredService<IInbox>().HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeFalse();
        foreach (var scope in new[] { first, second })
        {
            scope.GetRequiredService<IInbox>().Record(messageId, Consumer);
            scope.GetRequiredService<INotificationRepository>().Add(NewNotification(recipient, messageId));
        }

        var saved = await Task.WhenAll(
            first.GetRequiredService<IUnitOfWork>().SaveChangesUnlessInboxDuplicateAsync(Ct),
            second.GetRequiredService<IUnitOfWork>().SaveChangesUnlessInboxDuplicateAsync(Ct));

        saved.Count(result => result).ShouldBe(1);
        saved.Count(result => !result).ShouldBe(1);

        // The loser's notification rolled back with its inbox row, and its tracker was cleared.
        var loser = saved[0] ? second : first;
        loser.GetRequiredService<NotificationsDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
        var read = NewScope().GetRequiredService<NotificationsDbContext>();
        (await read.Set<Notification>().CountAsync(Ct)).ShouldBe(1);
        (await read.Set<InboxMessage>().CountAsync(Ct)).ShouldBe(1);
        (await NewScope().GetRequiredService<IInbox>().HasProcessedAsync(messageId, Consumer, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Duplicate_on_the_notification_index_is_not_an_inbox_duplicate()
    {
        var recipient = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SaveAsync(NewNotification(recipient, source));

        // A message id the inbox accepts, whose notification repeats the unique triple.
        var scope = NewScope();
        scope.GetRequiredService<IInbox>().Record(Guid.NewGuid(), Consumer);
        scope.GetRequiredService<INotificationRepository>().Add(NewNotification(recipient, source));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => scope.GetRequiredService<IUnitOfWork>().SaveChangesUnlessInboxDuplicateAsync(Ct));

        exception.InnerException.ShouldBeOfType<SqlException>().Number.ShouldBe(SqlUniqueIndexViolation);
        NotificationsInbox.IsDuplicate(exception).ShouldBeFalse();
        (await NewScope().GetRequiredService<NotificationsDbContext>().Set<InboxMessage>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Hub_ticket_is_consumed_once_by_two_concurrent_callers()
    {
        var userId = Guid.NewGuid();
        var hash = Hash(7);
        await SaveAsync(HubTicket.Issue(userId, hash, TimeSpan.FromSeconds(30), Now));

        var results = await Task.WhenAll(
            NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, Now, Ct),
            NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, Now, Ct));

        results.Count(result => result is null).ShouldBe(1);
        results.Single(result => result is not null).ShouldBe(userId);
        (await NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, Now, Ct)).ShouldBeNull();
        (await NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(Hash(8), Now, Ct)).ShouldBeNull();
        var stored = await NewScope().GetRequiredService<NotificationsDbContext>().Set<HubTicket>().SingleAsync(Ct);
        stored.ConsumedAt.ShouldBe(Now);
        stored.TokenHash.ShouldBe(hash);
    }

    [Fact]
    public async Task Expired_hub_ticket_is_not_consumed()
    {
        var userId = Guid.NewGuid();
        var hash = Hash(9);
        await SaveAsync(HubTicket.Issue(userId, hash, TimeSpan.FromSeconds(30), Now));

        // The ticket is usable strictly before its expiry; at the expiry instant it is already spent.
        var expiry = Now.AddSeconds(30);
        (await NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, expiry, Ct)).ShouldBeNull();
        (await NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, expiry.AddSeconds(1), Ct)).ShouldBeNull();
        (await NewScope().GetRequiredService<NotificationsDbContext>().Set<HubTicket>().SingleAsync(Ct)).ConsumedAt.ShouldBeNull();

        (await NewScope().GetRequiredService<IHubTicketRepository>().TryConsumeAsync(hash, expiry.AddMilliseconds(-1), Ct)).ShouldBe(userId);
    }

    [Fact]
    public async Task Mark_all_read_leaves_rows_created_after_now()
    {
        var userId = Guid.NewGuid();
        var old = NewInApp(userId, Now.AddMinutes(-10));
        var alreadyRead = NewInApp(userId, Now.AddMinutes(-9));
        alreadyRead.MarkRead(Now.AddMinutes(-8));
        var created = NewInApp(userId, Now.AddMinutes(1));
        var later = NewInApp(userId, Now.AddMinutes(6));
        var others = NewInApp(Guid.NewGuid(), Now.AddMinutes(-10));
        await SaveAsync(old, alreadyRead, created, later, others);

        // Created at exactly the cut-off counts (at or before now); later does not.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var marked = await NewScope().GetRequiredService<IInAppNotificationRepository>().MarkAllReadAsync(userId, Now, Ct);

        marked.ShouldBe(2);
        var rows = await NewScope().GetRequiredService<NotificationsDbContext>().Set<InAppNotification>().AsNoTracking().ToListAsync(Ct);
        rows.Single(row => row.Id == old.Id).ReadAt.ShouldBe(Now.UtcDateTime);
        rows.Single(row => row.Id == created.Id).ReadAt.ShouldBe(Now.UtcDateTime);
        rows.Single(row => row.Id == alreadyRead.Id).ReadAt.ShouldBe(Now.AddMinutes(-9).UtcDateTime);
        rows.Single(row => row.Id == later.Id).ReadAt.ShouldBeNull();
        rows.Single(row => row.Id == others.Id).ReadAt.ShouldBeNull();

        // A second call finds nothing unread to mark.
        (await NewScope().GetRequiredService<IInAppNotificationRepository>().MarkAllReadAsync(userId, Now, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task In_app_notification_is_found_only_by_its_owner()
    {
        var userId = Guid.NewGuid();
        var row = NewInApp(userId, Now);
        await SaveAsync(row);

        var repository = NewScope().GetRequiredService<IInAppNotificationRepository>();
        (await repository.GetForUserAsync(row.Id, userId, Ct)).ShouldNotBeNull().Title.ShouldBe("Title");
        (await repository.GetForUserAsync(row.Id, Guid.NewGuid(), Ct)).ShouldBeNull();
        (await repository.GetForUserAsync(Guid.NewGuid(), userId, Ct)).ShouldBeNull();

        // A second row for the same delivery id is refused by the key.
        var again = InAppNotification.Create(row.Id, userId, row.NotificationId, TypeCode, NotificationCategory.Security, "Again", "Body", Now);
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(again));
    }

    [Fact]
    public async Task Preferences_and_profile_round_trip()
    {
        var userId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var profile = UserNotificationProfile.Create(userId, Now);
        profile.SetQuietHours(QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(7, 30)).Value, Now);
        await SaveAsync(
            UserPreference.Create(userId, TypeCode, NotificationChannel.Email, isEnabled: false, Now),
            UserPreference.Create(userId, TypeCode, NotificationChannel.InApp, isEnabled: true, Now),
            UserPreference.Create(userId, "auth.user_locked_out", NotificationChannel.Email, isEnabled: false, Now),
            UserPreference.Create(other, TypeCode, NotificationChannel.Email, isEnabled: false, Now),
            profile,
            UserNotificationProfile.Create(other, Now));

        var preferences = await NewScope().GetRequiredService<IPreferenceRepository>().ListAsync(userId, Ct);
        preferences.Count.ShouldBe(3);
        preferences.Single(preference => preference.TypeCode == TypeCode && preference.Channel == NotificationChannel.Email).IsEnabled.ShouldBeFalse();
        preferences.Single(preference => preference.TypeCode == TypeCode && preference.Channel == NotificationChannel.InApp).IsEnabled.ShouldBeTrue();

        var savedProfile = (await NewScope().GetRequiredService<IPreferenceRepository>().GetSettingsAsync(userId, Ct)).ShouldNotBeNull();
        savedProfile.UserId.ShouldBe(userId);
        savedProfile.QuietHours.ShouldBe(new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 30)));
        savedProfile.RowVersion.ShouldNotBeEmpty();
        (await NewScope().GetRequiredService<IPreferenceRepository>().GetSettingsAsync(other, Ct)).ShouldNotBeNull().QuietHours.ShouldBeNull();
        (await NewScope().GetRequiredService<IPreferenceRepository>().GetSettingsAsync(Guid.NewGuid(), Ct)).ShouldBeNull();

        // Clearing the quiet hours writes nulls; a profile read before that change is refused by its row version.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var staleScope = NewScope();
        var stale = (await staleScope.GetRequiredService<IPreferenceRepository>().GetSettingsAsync(userId, Ct)).ShouldNotBeNull();
        var scope = NewScope();
        var tracked = (await scope.GetRequiredService<IPreferenceRepository>().GetSettingsAsync(userId, Ct)).ShouldNotBeNull();
        tracked.SetQuietHours(null, Now);
        await scope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        (await NewScope().GetRequiredService<IPreferenceRepository>().GetSettingsAsync(userId, Ct)).ShouldNotBeNull().QuietHours.ShouldBeNull();

        stale.SetQuietHours(new QuietHours(new TimeOnly(1, 0), new TimeOnly(2, 0)), Now);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleScope.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Schema_matches_the_design()
    {
        var context = NewScope().GetRequiredService<NotificationsDbContext>();

        // Every table is in schema notify and there is no outbox table (nothing here raises domain events).
        var tables = await StringsAsync(context, "SELECT name FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'notify' ORDER BY name");
        tables.ShouldBe(["Deliveries", "HubTickets", "InAppNotifications", "InboxMessages", "Notifications", "UserPreferences", "UserSettings", "__EFMigrationsHistory"], ignoreOrder: true);

        // Column types: enums as tinyint, the sizes of the domain constants, the ticket hash as varbinary(32).
        (await ColumnTypeAsync(context, "Notifications", "Priority")).ShouldBe("tinyint");
        (await ColumnTypeAsync(context, "Notifications", "TypeCode")).ShouldBe("nvarchar(100)");
        (await ColumnTypeAsync(context, "Notifications", "Culture")).ShouldBe("nvarchar(16)");
        (await ColumnTypeAsync(context, "Notifications", "Data")).ShouldBe("nvarchar(max)");
        (await ColumnTypeAsync(context, "Notifications", "ProtectedData")).ShouldBe("nvarchar(max)");
        (await ColumnTypeAsync(context, "Notifications", "CorrelationId")).ShouldBe("nvarchar(32)");
        (await ColumnTypeAsync(context, "Deliveries", "Channel")).ShouldBe("tinyint");
        (await ColumnTypeAsync(context, "Deliveries", "Status")).ShouldBe("tinyint");
        (await ColumnTypeAsync(context, "Deliveries", "Destination")).ShouldBe("nvarchar(320)");
        (await ColumnTypeAsync(context, "Deliveries", "LastError")).ShouldBe("nvarchar(200)");
        (await ColumnTypeAsync(context, "Deliveries", "RenderedSubject")).ShouldBe("nvarchar(300)");
        (await ColumnTypeAsync(context, "InAppNotifications", "Category")).ShouldBe("tinyint");
        (await ColumnTypeAsync(context, "InAppNotifications", "TypeCode")).ShouldBe("nvarchar(100)");
        (await ColumnTypeAsync(context, "InAppNotifications", "Title")).ShouldBe("nvarchar(200)");
        (await ColumnTypeAsync(context, "InAppNotifications", "Body")).ShouldBe("nvarchar(2000)");
        (await ColumnTypeAsync(context, "UserPreferences", "Channel")).ShouldBe("tinyint");
        (await ColumnTypeAsync(context, "UserPreferences", "TypeCode")).ShouldBe("nvarchar(100)");
        (await ColumnTypeAsync(context, "UserSettings", "RowVersion")).ShouldBe("timestamp");
        (await ColumnTypeAsync(context, "UserSettings", "QuietHoursStart")).ShouldBe("time");
        (await ColumnTypeAsync(context, "HubTickets", "TokenHash")).ShouldBe("varbinary(32)");

        // Primary keys.
        (await PrimaryKeyColumnsAsync(context, "Notifications")).ShouldBe(["Id"]);
        (await PrimaryKeyColumnsAsync(context, "UserPreferences")).ShouldBe(["UserId", "TypeCode", "Channel"]);
        (await PrimaryKeyColumnsAsync(context, "UserSettings")).ShouldBe(["UserId"]);
        (await PrimaryKeyColumnsAsync(context, "HubTickets")).ShouldBe(["TokenHash"]);
        (await PrimaryKeyColumnsAsync(context, "InboxMessages")).ShouldBe(["MessageId", "Consumer"]);
        (await StringsAsync(context, "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('notify.InboxMessages')")).ShouldBe(["PK_InboxMessages"]);

        // Indexes: key columns (DESC marked), included columns and filters.
        var indexes = await IndexesAsync(context);
        indexes["IX_Notifications_SourceMessageId_TypeCode_RecipientUserId"].ShouldBe("UNIQUE (SourceMessageId, TypeCode, RecipientUserId)");
        indexes["IX_Deliveries_Status_NextAttemptAt"].ShouldBe("(Status, NextAttemptAt) INCLUDE (Channel, LockedUntil) WHERE ([Status]=(0))");
        indexes["IX_Deliveries_CreatedAt_Id"].ShouldBe("(CreatedAt DESC, Id DESC)");
        indexes["IX_InAppNotifications_UserId_CreatedAt_Id"].ShouldBe("(UserId, CreatedAt DESC, Id DESC)");
        indexes["IX_InAppNotifications_UserId"].ShouldBe("(UserId) WHERE ([ReadAt] IS NULL)");
        indexes["IX_Deliveries_NotificationId"].ShouldBe("(NotificationId)");
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private static byte[] Hash(int seed) => SHA256.HashData(BitConverter.GetBytes(seed));

    private static async Task<string> ColumnTypeAsync(NotificationsDbContext context, string table, string column)
    {
        var rows = await StringsAsync(
            context,
            $"""
            SELECT CASE
                WHEN t.name = 'nvarchar' THEN 'nvarchar(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                WHEN t.name = 'varbinary' THEN 'varbinary(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                WHEN t.name = 'datetime2' THEN 'datetime2(' + CAST(c.scale AS varchar(10)) + ')'
                ELSE t.name END
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('notify.{table}') AND c.name = '{column}'
            """);

        return rows.Single();
    }

    private static Task<List<string>> PrimaryKeyColumnsAsync(NotificationsDbContext context, string table)
        => StringsAsync(
            context,
            $"""
            SELECT c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('notify.{table}') AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal
            """);

    /// <summary>The non-key indexes as index name to a readable definition: <c>[UNIQUE ](keys)[ INCLUDE (columns)][ WHERE filter]</c>.</summary>
    private static async Task<Dictionary<string, string>> IndexesAsync(NotificationsDbContext context)
    {
        var rows = await StringsAsync(
            context,
            """
            SELECT i.name, CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END, i.filter_definition, c.name, CAST(ic.is_descending_key AS int), CAST(ic.is_included_column AS int)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME((SELECT schema_id FROM sys.objects o WHERE o.object_id = i.object_id)) = 'notify' AND i.is_primary_key = 0 AND i.type > 0
            ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """,
            columns: 6);

        var definitions = new Dictionary<string, string>();
        foreach (var group in rows.GroupBy(row => row[0]))
        {
            var keys = group.Where(row => row[5] == "0").Select(row => row[3] + (row[4] == "1" ? " DESC" : string.Empty));
            var included = group.Where(row => row[5] == "1").Select(row => row[3]).ToList();
            var first = group.First();
            definitions[group.Key] = $"{first[1]}({string.Join(", ", keys)})"
                + (included.Count > 0 ? $" INCLUDE ({string.Join(", ", included)})" : string.Empty)
                + (first[2].Length > 0 ? $" WHERE {first[2]}" : string.Empty);
        }

        return definitions;
    }

    private static async Task<List<string>> StringsAsync(NotificationsDbContext context, string sql)
        => (await StringsAsync(context, sql, columns: 1)).Select(row => row[0]).ToList();

    private static async Task<List<string[]>> StringsAsync(NotificationsDbContext context, string sql, int columns)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new List<string[]>();
        while (await reader.ReadAsync(Ct))
        {
            result.Add([.. Enumerable.Range(0, columns).Select(index => reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty)]);
        }

        return result;
    }

    private Notification NewNotification(Guid recipient, Guid? source = null, string typeCode = TypeCode, DateTimeOffset? expiresAt = null)
        => Notification.Create(
            typeCode,
            NotificationPriority.Normal,
            recipient,
            "en",
            """{"displayName":"Alice"}""",
            protectedData: null,
            source ?? Guid.NewGuid(),
            correlationId: null,
            expiresAt,
            Now);

    private InAppNotification NewInApp(Guid userId, DateTimeOffset createdAt)
        => InAppNotification.Create(Guid.NewGuid(), userId, Guid.NewGuid(), TypeCode, NotificationCategory.Security, "Title", "Body", createdAt);

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private async Task SaveAsync(params object[] entities)
    {
        var context = NewScope().GetRequiredService<NotificationsDbContext>();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
