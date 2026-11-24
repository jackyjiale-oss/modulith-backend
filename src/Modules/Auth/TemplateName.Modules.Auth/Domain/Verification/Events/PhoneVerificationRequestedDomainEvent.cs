using System.Text;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification.Events;

/// <summary>
/// A phone verification code was issued; the outbox handler publishes it for the module that texts it. <c>ProtectedCode</c> is the code
/// encrypted by the application (ADR 0017), opaque to the domain and copied as it is into the integration event; the plain code is never
/// stored. <c>PhoneNumber</c> is the pending number in E.164.
/// </summary>
internal sealed record PhoneVerificationRequestedDomainEvent(
    Guid CodeId,
    Guid UserId,
    string PhoneNumber,
    string ProtectedCode,
    DateTimeOffset ExpiresAt) : IDomainEvent
{
    // The number and the code (even encrypted) never reach a log line.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("CodeId = ").Append(CodeId).Append(", UserId = ").Append(UserId).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}
