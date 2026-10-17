using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.Modules.Notifications.Infrastructure.Deliveries;

/// <summary>
/// The delivery worker (D14): claims due deliveries and sends each through the channel registered for it. Several workers (threads or
/// app instances) can run at once with the outbox's pattern (ADR 0007): each claims a batch under a lease with one statement, so no
/// transaction is held while it sends, and settles each delivery only while it still holds that lease. A worker starts no delivery once
/// less than <see cref="DeliveryOptions.LeaseSafetyMargin"/> of its lease is left, and on that or on shutdown it releases the deliveries
/// it claimed but did not start; its save fails if another worker re-claimed a delivery, so it never overwrites that outcome.
/// </summary>
/// <remarks>
/// <para>
/// Per delivery, in its own scope: an expired one (<c>ExpiresAt</c> reached) is <see cref="DeliveryStatus.Expired"/> and never rendered;
/// otherwise its variables are read (an email's protected values decrypted, in memory only), the message is rendered in the
/// notification's culture and handed to the channel. The outcome is <c>Sent</c>, a retry on <see cref="DeliveryRetryPolicy"/>'s
/// schedule, or <c>DeadLettered</c> (a permanent failure, or the last attempt). Rows a channel staged (the in-app row) are saved in the
/// same save as the outcome, and its after-save work (the live push) runs only once that save succeeded.
/// </para>
/// <para>
/// Attempts are counted when a delivery is claimed, so an attempt that ends without an outcome (the process dies mid-send) still
/// counts, while a released delivery gets its attempt back; a delivery claimed again after <see cref="DeliveryOptions.MaxAttempts"/> is dead-lettered
/// (<c>attempts_exhausted</c>) instead of being sent for ever. Delivery is at least once for email (a crash after the server accepted
/// the message sends it again) and exactly once for in-app (the row's key is the delivery id).
/// </para>
/// <para>
/// No address, link, variable value or rendered text is ever logged, stored in <c>LastError</c> or put in a metric tag: failures are
/// recorded as short reason codes.
/// </para>
/// </remarks>
internal sealed partial class DeliveryDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<DeliveryOptions> options,
    TimeProvider timeProvider,
    INotificationsMetrics metrics,
    ILogger<DeliveryDispatcher> logger)
{
    /// <summary>No <see cref="INotificationChannel"/> is registered for the delivery's channel.</summary>
    internal const string ChannelUnavailable = "channel_unavailable";

    /// <summary>The message could not be rendered: missing or broken template, unreadable variables or protected values.</summary>
    internal const string RenderFailed = "render_failed";

    /// <summary>The channel threw instead of answering; treated as transient.</summary>
    internal const string ChannelError = "channel_error";

    /// <summary>The delivery was claimed again after its last allowed attempt ended without an outcome.</summary>
    internal const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>What is stored and logged instead of a channel's reason that is not a short code (it could quote an address or a link).</summary>
    internal const string UnknownReason = "unknown";

    /// <summary>The ceiling on saving an outcome, which is not cancelled by shutdown (see <c>ProcessAsync</c>).</summary>
    internal static readonly TimeSpan OutcomeSaveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The ceiling on releasing the unstarted rest of a batch, which is not cancelled by shutdown either.</summary>
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    // UPDLOCK + READPAST: concurrent claims skip each other's rows instead of waiting or taking the same ones. The lease (LockedUntil)
    // keeps the claimed rows hidden after this statement commits; it expires if the claiming process dies. The attempt is counted here.
    // Status = 0 (Pending) is a literal, not a parameter, so the filtered index IX_Deliveries_Status_NextAttemptAt serves the claim.
    private const string ClaimSqlTemplate = """
        WITH batch AS (
            SELECT TOP (@BatchSize) Id, AttemptCount, LockedUntil
            FROM {deliveries} WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Status = 0 AND NextAttemptAt <= @Now
              AND (LockedUntil IS NULL OR LockedUntil < @Now)
            ORDER BY NextAttemptAt)
        UPDATE batch SET LockedUntil = @LeaseUntil, AttemptCount = AttemptCount + 1
        OUTPUT inserted.Id, inserted.AttemptCount, inserted.LockedUntil;
        """;

    // Gives claimed-but-unstarted deliveries back: no lease, and the attempt the claim counted is undone. Only rows that are still pending
    // under this worker's lease are touched, so a delivery another worker has re-claimed (or settled) is left alone.
    private const string ReleaseSqlTemplate = """
        UPDATE {deliveries} SET LockedUntil = NULL, AttemptCount = AttemptCount - 1
        WHERE Id IN ({ids}) AND LockedUntil = @LeaseUntil AND Status = 0;
        """;

    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly DeliveryOptions _options = options.Value;
    private string? _deliveriesTable;

    /// <summary>
    /// Claims up to <see cref="DeliveryOptions.BatchSize"/> due deliveries and processes each in its own scope, stopping early once less
    /// than <see cref="DeliveryOptions.LeaseSafetyMargin"/> of the lease is left. A delivery that fails unexpectedly (a database error) is
    /// logged and left to its lease; the others go on. Cancelling <paramref name="cancellationToken"/> (shutdown) stops before the next
    /// delivery and interrupts a send in progress, but never the save of an outcome the channel has already given. Whenever the batch
    /// stops early, the deliveries it did not start are released (see <c>ReleaseAsync</c>), so the next poll claims them at once.
    /// </summary>
    /// <returns>The number of deliveries claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimBatchAsync(cancellationToken);
        var unavailableChannels = new HashSet<NotificationChannel>();
        var started = 0;

        try
        {
            while (started < claimed.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var claim = claimed[started];

                // Too little lease left to finish a send before another worker may re-claim the delivery: give the rest back.
                if (claim.LockedUntil - timeProvider.GetUtcNow().UtcDateTime < _options.LeaseSafetyMargin)
                {
                    LogLeaseEnding(logger, claim.Id, claimed.Count - started);
                    break;
                }

                started++;
                try
                {
                    await ProcessAsync(claim, unavailableChannels, cancellationToken);
                }
                catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
                {
                    LogDeliveryFailed(logger, exception, claim.Id, claim.AttemptCount);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(claimed, started);
            throw;
        }

        await ReleaseAsync(claimed, started);
        return claimed.Count;
    }

    private async Task<List<ClaimedDelivery>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await context.Database
            .SqlQueryRaw<ClaimedDelivery>(
                ClaimSqlTemplate.Replace("{deliveries}", DeliveriesTable(context), StringComparison.Ordinal),
                new SqlParameter("@BatchSize", SqlDbType.Int) { Value = _options.BatchSize },
                new SqlParameter("@Now", SqlDbType.DateTime2) { Scale = 3, Value = now },
                new SqlParameter("@LeaseUntil", SqlDbType.DateTime2) { Scale = 3, Value = now + _options.LeaseDuration })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the claimed deliveries from <paramref name="firstUnstarted"/> on, in one statement: they become claimable at once and
    /// get back the attempt their claim counted. Like an outcome's save it is not cancelled by shutdown, only bounded by
    /// <see cref="ReleaseTimeout"/>; a failure is logged (without values) and ignored, because the lease still expires on its own.
    /// </summary>
    private async Task ReleaseAsync(List<ClaimedDelivery> claimed, int firstUnstarted)
    {
        if (firstUnstarted >= claimed.Count)
        {
            return;
        }

        var unstarted = claimed[firstUnstarted..];
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var parameters = unstarted
                .Select((claim, index) => new SqlParameter($"@Id{index}", SqlDbType.UniqueIdentifier) { Value = claim.Id })
                .Append(new SqlParameter("@LeaseUntil", SqlDbType.DateTime2) { Scale = 3, Value = unstarted[0].LockedUntil })
                .ToArray<object>();
            var sql = ReleaseSqlTemplate
                .Replace("{deliveries}", DeliveriesTable(context), StringComparison.Ordinal)
                .Replace("{ids}", string.Join(", ", unstarted.Select((_, index) => $"@Id{index}")), StringComparison.Ordinal);

            using var timeout = new CancellationTokenSource(ReleaseTimeout, timeProvider);
            await context.Database.ExecuteSqlRawAsync(sql, parameters, timeout.Token);
            LogReleased(logger, unstarted.Count);
        }
        catch (Exception exception)
        {
            LogReleaseFailed(logger, unstarted.Count, exception.GetType().Name);
        }
    }

    private async Task ProcessAsync(ClaimedDelivery claim, HashSet<NotificationChannel> unavailableChannels, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<NotificationsDbContext>();
        var notifications = services.GetRequiredService<INotificationRepository>();

        var notification = await notifications.GetByDeliveryIdAsync(claim.Id, cancellationToken);
        if (notification is null || OwnedDelivery(notification, claim) is not { } delivery)
        {
            // Settled or re-claimed by another worker after this lease ran out.
            LogLeaseLost(logger, claim.Id);
            return;
        }

        DeliveryResult result;
        string? subject = null;
        var now = timeProvider.GetUtcNow();
        if (delivery.ExpiresAt is { } expiresAt && now.UtcDateTime >= expiresAt)
        {
            delivery.Expire();
            result = DeliveryResult.Expired;
        }
        else
        {
            ChannelSendResult sendResult;
            if (claim.AttemptCount > _options.MaxAttempts)
            {
                sendResult = ChannelSendResult.Permanent(AttemptsExhausted);
            }
            else
            {
                (sendResult, subject) = await SendAsync(services, notification, delivery, unavailableChannels, cancellationToken);
            }

            now = timeProvider.GetUtcNow();
            result = Settle(delivery, sendResult, subject, claim.AttemptCount, now);
        }

        // Once the channel has answered, the message may be out: the outcome is saved even when the host is stopping, because cancelling
        // the save would leave the delivery leased and send it again after the lease. A fixed ceiling bounds the save instead.
        using var saveTimeout = new CancellationTokenSource(OutcomeSaveTimeout, timeProvider);
        if (!await SaveAsync(context, notifications, claim, subject, now, saveTimeout.Token))
        {
            return;
        }

        metrics.RecordDelivery(delivery.Channel, result, timeProvider.GetElapsedTime(startedAt));
        LogOutcome(delivery, result, claim.AttemptCount);
        await services.GetRequiredService<DeliverySaveCallbacks>().RunAsync(cancellationToken);
    }

    /// <summary>Renders the message and hands it to the delivery's channel; returns the channel's answer and the rendered subject.</summary>
    private async Task<(ChannelSendResult Result, string? Subject)> SendAsync(
        IServiceProvider services,
        Notification notification,
        Delivery delivery,
        HashSet<NotificationChannel> unavailableChannels,
        CancellationToken cancellationToken)
    {
        var channel = services.GetServices<INotificationChannel>().FirstOrDefault(candidate => candidate.Channel == delivery.Channel);
        if (channel is null)
        {
            // A configuration error that hits every delivery of the channel: say it once per batch.
            if (unavailableChannels.Add(delivery.Channel))
            {
                LogChannelUnavailable(logger, delivery.Channel);
            }

            return (ChannelSendResult.Permanent(ChannelUnavailable), null);
        }

        if (services.GetRequiredService<NotificationCatalog>().Find(notification.TypeCode) is not { } type)
        {
            LogTypeUnknown(logger, delivery.Id, notification.TypeCode);
            return (ChannelSendResult.Permanent(RenderFailed), null);
        }

        RenderedMessage message;
        try
        {
            var variables = Variables(notification, delivery.Channel, services.GetRequiredService<ISecretProtector>());
            message = services.GetRequiredService<INotificationRenderer>().Render(notification.TypeCode, delivery.Channel, notification.Culture, variables);
        }
        catch (TemplateRenderException exception)
        {
            // The exception names templates, parts and variable names only, never a value (its contract).
            LogRenderFailed(logger, exception, delivery.Id);
            return (ChannelSendResult.Permanent(RenderFailed), null);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Not the exception itself: only its type, so nothing of the stored values can reach the log.
            LogDataUnreadable(logger, delivery.Id, notification.TypeCode, exception.GetType().Name);
            return (ChannelSendResult.Permanent(RenderFailed), null);
        }

        var work = new DeliveryWork(
            delivery.Id, notification.Id, notification.RecipientUserId, notification.TypeCode, type.Category, delivery.Destination, message);
        try
        {
            return (await channel.SendAsync(work, cancellationToken), message.Subject);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            // A channel reports expected failures as results; an exception may quote an address, so only its type is logged.
            LogChannelThrew(logger, delivery.Channel, exception.GetType().Name, delivery.Id);
            return (ChannelSendResult.Transient(ChannelError), null);
        }
    }

    private DeliveryResult Settle(Delivery delivery, ChannelSendResult sendResult, string? subject, int attempt, DateTimeOffset now)
    {
        // A channel can build a result around the factories' check (the record's constructor, a with expression); LastError and the
        // logs get only a short code.
        var reason = ChannelSendResult.IsReasonCode(sendResult.Reason) ? sendResult.Reason : UnknownReason;
        switch (sendResult.Outcome)
        {
            case ChannelSendOutcome.Sent:
                delivery.MarkSent(subject, now);
                return DeliveryResult.Sent;

            case ChannelSendOutcome.TransientFailure
                when DeliveryRetryPolicy.NextAttemptAt(attempt, now.UtcDateTime, _options.MaxAttempts) is { } nextAttemptAt:
                delivery.ScheduleRetry(reason, nextAttemptAt);
                return DeliveryResult.Retry;

            default:
                delivery.DeadLetter(reason, now);
                return DeliveryResult.DeadLettered;
        }
    }

    /// <summary>
    /// Saves the outcome (and any row a channel staged) in one transaction, guarded by the lease: <c>LockedUntil</c> is the delivery's
    /// concurrency token. Returns false when the lease was lost, in which case nothing was saved.
    /// </summary>
    private async Task<bool> SaveAsync(
        NotificationsDbContext context,
        INotificationRepository notifications,
        ClaimedDelivery claim,
        string? subject,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            LogLeaseLost(logger, claim.Id);
            return false;
        }
        catch (DbUpdateException exception) when (IsDuplicateInAppRow(exception))
        {
            // The delivery's in-app row already exists (its key is the delivery id), so the user has the notification: the delivery is
            // sent. The failed save rolled back as a whole; settle it again without the row.
            context.ChangeTracker.Clear();
            var notification = await notifications.GetByDeliveryIdAsync(claim.Id, cancellationToken);
            if (notification is null || OwnedDelivery(notification, claim) is not { } delivery)
            {
                LogLeaseLost(logger, claim.Id);
                return false;
            }

            LogInAppRowExists(logger, claim.Id);
            delivery.MarkSent(subject, settledAt);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                LogLeaseLost(logger, claim.Id);
                return false;
            }
        }
    }

    // Still pending and still under the lease this worker claimed.
    private static Delivery? OwnedDelivery(Notification notification, ClaimedDelivery claim)
        => notification.Deliveries.SingleOrDefault(candidate => candidate.Id == claim.Id) is { Status: DeliveryStatus.Pending } delivery
            && delivery.LockedUntil == claim.LockedUntil
            ? delivery
            : null;

    // The non-secret variables, plus an email's secret ones decrypted (in memory only). In-app templates never use a secret.
    private static Dictionary<string, string> Variables(Notification notification, NotificationChannel channel, ISecretProtector secretProtector)
    {
        var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.Data)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (channel == NotificationChannel.Email && notification.ProtectedData is { } protectedData)
        {
            foreach (var (name, protectedValue) in JsonSerializer.Deserialize<Dictionary<string, string>>(protectedData) ?? [])
            {
                variables[name] = secretProtector.Unprotect(protectedValue);
            }
        }

        return variables;
    }

    private void LogOutcome(Delivery delivery, DeliveryResult result, int attempt)
    {
        switch (result)
        {
            case DeliveryResult.Sent:
                LogSent(logger, delivery.Id, delivery.Channel, attempt);
                break;
            case DeliveryResult.Retry:
                LogRetryScheduled(logger, delivery.Id, delivery.Channel, delivery.LastError, attempt, delivery.NextAttemptAt);
                break;
            case DeliveryResult.DeadLettered:
                LogDeadLettered(logger, delivery.Id, delivery.Channel, delivery.LastError, attempt);
                break;
            case DeliveryResult.Expired:
                LogExpired(logger, delivery.Id, delivery.Channel);
                break;
        }
    }

    // The deliveries table as the model maps it, delimited (for example [notify].[Deliveries]).
    private string DeliveriesTable(NotificationsDbContext context)
    {
        if (_deliveriesTable is null)
        {
            var entityType = context.Model.FindEntityType(typeof(Delivery))
                ?? throw new InvalidOperationException($"{nameof(NotificationsDbContext)} does not map the deliveries.");
            _deliveriesTable = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());
        }

        return _deliveriesTable;
    }

    private static bool IsDuplicateInAppRow(DbUpdateException exception)
        => exception.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation }
            && exception.Entries.Any(entry => entry.Entity is InAppNotification);

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Delivery {DeliveryId} was sent through {Channel}, attempt {Attempt}")]
    private static partial void LogSent(ILogger logger, Guid deliveryId, NotificationChannel channel, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} through {Channel} failed with {Reason}, attempt {Attempt}; next attempt at {NextAttemptAt}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid deliveryId, NotificationChannel channel, string? reason, int attempt, DateTime? nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} through {Channel} was dead-lettered with {Reason}, attempt {Attempt}")]
    private static partial void LogDeadLettered(ILogger logger, Guid deliveryId, NotificationChannel channel, string? reason, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery {DeliveryId} through {Channel} expired before it was sent")]
    private static partial void LogExpired(ILogger logger, Guid deliveryId, NotificationChannel channel);

    [LoggerMessage(Level = LogLevel.Error, Message = "No notification channel is registered for {Channel}; its deliveries are dead-lettered with channel_unavailable")]
    private static partial void LogChannelUnavailable(ILogger logger, NotificationChannel channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} could not be rendered")]
    private static partial void LogRenderFailed(ILogger logger, Exception exception, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} could not be rendered: notification type {TypeCode} is not declared")]
    private static partial void LogTypeUnknown(ILogger logger, Guid deliveryId, string typeCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} of notification type {TypeCode} could not be rendered: its stored variables could not be read ({ExceptionType})")]
    private static partial void LogDataUnreadable(ILogger logger, Guid deliveryId, string typeCode, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Channel} channel threw {ExceptionType} on delivery {DeliveryId}; the attempt counts as a transient failure")]
    private static partial void LogChannelThrew(ILogger logger, NotificationChannel channel, string exceptionType, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The in-app row of delivery {DeliveryId} already exists; the delivery is settled as sent")]
    private static partial void LogInAppRowExists(ILogger logger, Guid deliveryId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} could not be processed, attempt {Attempt}; it is retried once its lease runs out")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, Guid deliveryId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery lease ends too soon to start delivery {DeliveryId}; releasing the {Count} deliveries of the batch not started")]
    private static partial void LogLeaseEnding(ILogger logger, Guid deliveryId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Released {Count} claimed deliveries that were not started")]
    private static partial void LogReleased(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing {Count} claimed deliveries that were not started failed ({ExceptionType}); they are claimed again once their lease runs out")]
    private static partial void LogReleaseFailed(ILogger logger, int count, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery lease on delivery {DeliveryId} was lost to another worker; its outcome stands")]
    private static partial void LogLeaseLost(ILogger logger, Guid deliveryId);

    /// <summary>The columns the claim returns: <see cref="AttemptCount"/> includes this attempt, <see cref="LockedUntil"/> is the lease held.</summary>
    private sealed record ClaimedDelivery(Guid Id, int AttemptCount, DateTime LockedUntil);
}
