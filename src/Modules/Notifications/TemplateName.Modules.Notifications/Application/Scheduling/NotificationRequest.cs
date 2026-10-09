namespace TemplateName.Modules.Notifications.Application.Scheduling;

/// <summary>
/// What a consumer asks <see cref="NotificationScheduler"/> to create for one integration event.
/// </summary>
/// <param name="TypeCode">The catalog code of the notification type.</param>
/// <param name="RecipientUserId">The user to notify; the scheduler looks up the contact details.</param>
/// <param name="SourceMessageId">The integration event id: with <paramref name="Consumer"/> the inbox key, so a repeat has no effect.</param>
/// <param name="Consumer">The inbox consumer name: the consuming handler's full type name (at most 400 characters).</param>
/// <param name="Variables">
/// The type's non-secret variables the event supplies, already formatted as text. The scheduler adds <c>display_name</c> and the
/// recipient's own variables; together they must be exactly the type's declared variables.
/// </param>
/// <param name="ProtectedVariables">
/// The type's secret variables (single-use links), each already <c>ISecretProtector</c> ciphertext from the event. They are stored as
/// given and must be exactly the type's declared secret variables.
/// </param>
/// <param name="EmailOverride">The address to email instead of the recipient's current one (the address a link was issued for).</param>
/// <param name="ExpiresAt">When the notification stops being worth delivering (a link's expiry); <see langword="null"/> never.</param>
internal sealed record NotificationRequest(
    string TypeCode,
    Guid RecipientUserId,
    Guid SourceMessageId,
    string Consumer,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyDictionary<string, string> ProtectedVariables,
    string? EmailOverride,
    DateTimeOffset? ExpiresAt);
