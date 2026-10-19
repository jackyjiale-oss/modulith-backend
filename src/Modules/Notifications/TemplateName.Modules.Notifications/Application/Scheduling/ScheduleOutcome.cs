namespace TemplateName.Modules.Notifications.Application.Scheduling;

/// <summary>What <see cref="NotificationScheduler.ScheduleAsync"/> did with a request. Every outcome leaves the event recorded in the inbox.</summary>
internal enum ScheduleOutcome
{
    /// <summary>The notification and its deliveries were created.</summary>
    Scheduled,

    /// <summary>The consumer had already processed the event (an earlier delivery, or a concurrent one that saved first); nothing was written.</summary>
    AlreadyProcessed,

    /// <summary>The recipient does not exist (or was deleted); only the inbox record was written.</summary>
    RecipientNotFound,

    /// <summary>
    /// A protected link could not be decrypted or is not an absolute <c>http</c> or <c>https</c> URL; only the inbox record was written,
    /// so the event is not retried.
    /// </summary>
    Refused,

    /// <summary>
    /// No channel was left once the recipient's preferences and the destination check were applied; only the inbox record was written.
    /// </summary>
    NothingToDeliver,
}
