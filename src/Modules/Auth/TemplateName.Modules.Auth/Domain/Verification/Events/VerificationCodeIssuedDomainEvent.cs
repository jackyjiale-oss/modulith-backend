using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification.Events;

/// <summary>
/// A verification code was issued; one handler sends the email and another publishes the integration event. <c>ProtectedToken</c> is
/// the token encrypted by the application (ADR 0017), opaque to the domain; the handlers decrypt it to build the link. It is never
/// stored on the aggregate. <c>Trigger</c> says who caused the code; it is the last member and defaults to
/// <see cref="VerificationTrigger.SelfService"/>, so an outbox row written before it existed still reads.
/// </summary>
internal sealed record VerificationCodeIssuedDomainEvent(
    Guid CodeId,
    Guid UserId,
    VerificationPurpose Purpose,
    string Target,
    string ProtectedToken,
    VerificationTrigger Trigger = VerificationTrigger.SelfService) : IDomainEvent;
