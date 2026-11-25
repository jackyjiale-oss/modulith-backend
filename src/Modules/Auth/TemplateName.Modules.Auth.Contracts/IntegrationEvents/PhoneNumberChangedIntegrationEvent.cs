using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A user's verified phone number, which can sign the account in, was added, replaced or removed; the user should be told in case it was
/// not them. It never carries the number: only its last three digits, so the owner can recognise it.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the change happened (UTC).</param>
/// <param name="UserId">The user whose number changed. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="Kind">What happened.</param>
/// <param name="LastDigits">The last three digits of the new number, or of the removed one.</param>
public sealed record PhoneNumberChangedIntegrationEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    PhoneNumberChangeKind Kind,
    string LastDigits) : IIntegrationEvent;
