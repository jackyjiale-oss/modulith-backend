using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Scheduling;

/// <summary>
/// Turns one consumed integration event into a <see cref="Notification"/> and its deliveries, idempotently (ADR 0018, D3). It only
/// writes rows; the delivery worker sends them. Steps, in order:
/// <list type="number">
/// <item>An unknown type code, or secret variables other than the type's, throw <see cref="InvalidOperationException"/> (a programming error).</item>
/// <item>An event the consumer has already processed returns <see cref="ScheduleOutcome.AlreadyProcessed"/> at once.</item>
/// <item>
/// Every protected value is decrypted in memory, only to check that it is an absolute <c>http</c> or <c>https</c> URL: a template puts
/// it in an <c>href</c>, where HTML encoding does not stop a <c>javascript:</c> link. The plaintext is dropped at once; what is stored
/// is the event's ciphertext, unchanged. An unreadable or unsafe value records the event and returns
/// <see cref="ScheduleOutcome.Refused"/> (a Warning names only the event id and type), so a poison event is not retried for ever.
/// </item>
/// <item>The recipient is looked up once in <see cref="IUserContactDirectory"/>; none records the event and returns <see cref="ScheduleOutcome.RecipientNotFound"/>.</item>
/// <item>
/// The culture (<see cref="RecipientCulture"/>) and the variables (the request's, <c>display_name</c> when the type declares it, and
/// the <c>contactVariables</c> callback's, such as local times) are snapshotted; together they must be exactly the type's variables.
/// </item>
/// <item>
/// The channels are the type's defaults minus those the recipient switched off (a mandatory channel is always kept). An email goes to
/// the request's <see cref="NotificationRequest.EmailOverride"/> (the address a link was issued for), else the contact's address, and
/// is skipped with a Warning naming only the user id when that is not one valid address (<see cref="EmailDestination"/>).
/// </item>
/// <item>
/// Every delivery is due now, except an email of a type that is not <see cref="NotificationPriority.Critical"/> inside the recipient's
/// quiet hours, which waits for their end in the recipient's time zone (<see cref="TimeZoneResolver"/>; an unusable zone means UTC
/// and logs a Warning naming only the user id). In-app deliveries are never deferred.
/// </item>
/// <item>
/// The inbox record and the rows are saved together with <see cref="IUnitOfWork.SaveChangesUnlessInboxDuplicateAsync"/>: a concurrent
/// delivery of the same event that saved first makes this one return <see cref="ScheduleOutcome.AlreadyProcessed"/> with nothing written.
/// </item>
/// </list>
/// Anything else that throws is an infrastructure failure, which the publishing module's outbox retries. No address, link or variable
/// value is ever logged or put in an exception message.
/// </summary>
internal sealed partial class NotificationScheduler(
    NotificationCatalog catalog,
    IInbox inbox,
    IUserContactDirectory contacts,
    IPreferenceRepository preferences,
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    ISecretProtector secretProtector,
    INotificationsMetrics metrics,
    TimeProvider timeProvider,
    ILogger<NotificationScheduler> logger)
{
    private const string DisplayNameVariable = "display_name";

    public async Task<ScheduleOutcome> ScheduleAsync(
        NotificationRequest request,
        Func<UserContact, IReadOnlyDictionary<string, string>>? contactVariables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var type = catalog.Find(request.TypeCode)
            ?? throw new InvalidOperationException($"Notification type '{request.TypeCode}' is not declared by any {nameof(INotificationTypeSource)}.");
        EnsureExactly(type, "secret variables", type.SecretVariables, request.ProtectedVariables.Keys);

        if (await inbox.HasProcessedAsync(request.SourceMessageId, request.Consumer, cancellationToken))
        {
            return ScheduleOutcome.AlreadyProcessed;
        }

        if (!request.ProtectedVariables.Values.All(IsSafeLink))
        {
            LogRefusedLink(logger, request.SourceMessageId, type.Code);
            return await RecordOnlyAsync(request, ScheduleOutcome.Refused, cancellationToken);
        }

        var contact = await contacts.FindAsync(request.RecipientUserId, cancellationToken);
        if (contact is null)
        {
            LogRecipientNotFound(logger, request.RecipientUserId);
            return await RecordOnlyAsync(request, ScheduleOutcome.RecipientNotFound, cancellationToken);
        }

        var variables = Variables(type, request, contact, contactVariables);
        var (zone, isFallback) = TimeZoneResolver.Resolve(contact.TimeZone);
        if (isFallback)
        {
            LogTimeZoneFallback(logger, request.RecipientUserId);
        }

        var deliveries = await DeliveriesAsync(type, request, contact, cancellationToken);
        if (deliveries.Count == 0)
        {
            LogNothingToDeliver(logger, request.SourceMessageId, type.Code);
            return await RecordOnlyAsync(request, ScheduleOutcome.NothingToDeliver, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var emailDueAt = deliveries.Exists(delivery => delivery.Channel == NotificationChannel.Email)
            ? await EmailDueAtAsync(type, request.RecipientUserId, zone, now, cancellationToken)
            : now;

        var notification = Notification.Create(
            type.Code,
            type.Priority,
            request.RecipientUserId,
            RecipientCulture.Resolve(contact.Locale),
            Serialize(variables),
            request.ProtectedVariables.Count == 0 ? null : Serialize(request.ProtectedVariables),
            request.SourceMessageId,
            Activity.Current?.TraceId.ToHexString(),
            request.ExpiresAt,
            now);
        foreach (var (channel, destination) in deliveries)
        {
            notification.AddDelivery(channel, destination, channel == NotificationChannel.Email ? emailDueAt : now, now);
        }

        notifications.Add(notification);
        inbox.Record(request.SourceMessageId, request.Consumer);
        if (!await unitOfWork.SaveChangesUnlessInboxDuplicateAsync(cancellationToken))
        {
            return ScheduleOutcome.AlreadyProcessed;
        }

        metrics.RecordCreated(type.Code);
        return ScheduleOutcome.Scheduled;
    }

    private static Dictionary<string, string> Variables(
        NotificationTypeDefinition type,
        NotificationRequest request,
        UserContact contact,
        Func<UserContact, IReadOnlyDictionary<string, string>>? contactVariables)
    {
        var variables = new Dictionary<string, string>(request.Variables, StringComparer.Ordinal);
        if (type.Variables.Contains(DisplayNameVariable))
        {
            variables[DisplayNameVariable] = contact.DisplayName;
        }

        if (contactVariables is not null)
        {
            foreach (var (name, value) in contactVariables(contact))
            {
                variables[name] = value;
            }
        }

        EnsureExactly(type, "variables", type.Variables, variables.Keys);
        return variables;
    }

    // The names only: a value may be personal data or a secret.
    private static void EnsureExactly(NotificationTypeDefinition type, string kind, IReadOnlySet<string> declared, IEnumerable<string> supplied)
    {
        var suppliedNames = supplied.ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(suppliedNames))
        {
            throw new InvalidOperationException(
                $"Notification type '{type.Code}' declares the {kind} [{Names(declared)}] but was given [{Names(suppliedNames)}].");
        }

        static string Names(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));
    }

    private static string Serialize(IReadOnlyDictionary<string, string> values)
        => JsonSerializer.Serialize(new SortedDictionary<string, string>(values.ToDictionary(), StringComparer.Ordinal));

    // Decrypts in memory only to check the scheme. The plaintext never leaves this method: it is not stored, logged or put in an
    // exception (a CryptographicException message carries no plaintext, and it is not rethrown).
    private bool IsSafeLink(string ciphertext)
    {
        string link;
        try
        {
            link = secretProtector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0;
    }

    private async Task<List<(NotificationChannel Channel, string? Destination)>> DeliveriesAsync(
        NotificationTypeDefinition type,
        NotificationRequest request,
        UserContact contact,
        CancellationToken cancellationToken)
    {
        // Only a type with an optional channel can have a preference that changes anything (D10).
        var switchedOff = type.IsConfigurable
            ? (await preferences.ListAsync(request.RecipientUserId, cancellationToken))
                .Where(preference => preference.TypeCode == type.Code && !preference.IsEnabled)
                .Select(preference => preference.Channel)
                .ToHashSet()
            : [];

        var deliveries = new List<(NotificationChannel Channel, string? Destination)>();
        foreach (var channel in type.DefaultChannels.Where(channel => type.IsMandatory(channel) || !switchedOff.Contains(channel)))
        {
            if (channel != NotificationChannel.Email)
            {
                deliveries.Add((channel, null));
                continue;
            }

            var address = request.EmailOverride ?? contact.Email;
            if (EmailDestination.IsValid(address))
            {
                deliveries.Add((channel, address));
            }
            else
            {
                LogInvalidEmailAddress(logger, request.RecipientUserId);
            }
        }

        return deliveries;
    }

    private async Task<DateTimeOffset> EmailDueAtAsync(
        NotificationTypeDefinition type,
        Guid userId,
        TimeZoneInfo zone,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (type.Priority == NotificationPriority.Critical)
        {
            return now;
        }

        var profile = await preferences.GetSettingsAsync(userId, cancellationToken);
        return profile?.QuietHours is { } quietHours ? quietHours.NextAllowedAt(now, zone) : now;
    }

    private async Task<ScheduleOutcome> RecordOnlyAsync(NotificationRequest request, ScheduleOutcome outcome, CancellationToken cancellationToken)
    {
        inbox.Record(request.SourceMessageId, request.Consumer);
        return await unitOfWork.SaveChangesUnlessInboxDuplicateAsync(cancellationToken) ? outcome : ScheduleOutcome.AlreadyProcessed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused integration event {MessageId} for notification type {TypeCode}: a protected link is unreadable or not an absolute http or https URL")]
    private static partial void LogRefusedLink(ILogger logger, Guid messageId, string typeCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created no notification for user {UserId}: the user does not exist")]
    private static partial void LogRecipientNotFound(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The time zone of user {UserId} is empty or unknown; UTC is used")]
    private static partial void LogTimeZoneFallback(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the email delivery for user {UserId}: the address is not one valid email address")]
    private static partial void LogInvalidEmailAddress(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created no notification for integration event {MessageId} of notification type {TypeCode}: no channel is left to deliver it")]
    private static partial void LogNothingToDeliver(ILogger logger, Guid messageId, string typeCode);
}
