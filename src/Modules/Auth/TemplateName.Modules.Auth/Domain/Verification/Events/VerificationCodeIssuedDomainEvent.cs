using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification.Events;

/// <summary>
/// A verification code was issued; a handler sends the email. <c>ProtectedToken</c> is the token encrypted by the application
/// (ADR 0017), opaque to the domain; the handler decrypts it to build the link. It is never stored on the aggregate.
/// </summary>
internal sealed record VerificationCodeIssuedDomainEvent(
    Guid CodeId,
    Guid UserId,
    VerificationPurpose Purpose,
    string Target,
    string ProtectedToken) : IDomainEvent;
