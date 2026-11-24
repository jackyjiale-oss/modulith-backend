using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>
/// The account's verified phone number was added, replaced or removed. It carries the last three digits only (of the new number, or of
/// the removed one), never the number, so the owner can recognise it in a notice.
/// </summary>
internal sealed record PhoneNumberChangedDomainEvent(Guid UserId, PhoneNumberChange Change, string LastDigits, DateTimeOffset ChangedAt)
    : IDomainEvent;
