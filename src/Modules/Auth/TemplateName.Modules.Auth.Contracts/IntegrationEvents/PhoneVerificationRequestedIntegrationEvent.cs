using System.Text;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A code to verify a phone number was issued and must be texted to that number. Published once per issued code; a new request
/// publishes another one and ends the previous code.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the code was issued (UTC).</param>
/// <param name="UserId">The user the code belongs to.</param>
/// <param name="PhoneNumber">The number the code is for, in E.164 (<c>+60123456789</c>): where it must be sent. It is not yet the user's verified number.</param>
/// <param name="ProtectedCode">
/// The 6-digit code, encrypted with <c>ISecretProtector</c>. It is single-use and secret: store it only as this ciphertext, decrypt it
/// only in memory (to check it, and while rendering the message), and never store, log or put the plaintext in an exception or an
/// in-app text.
/// </param>
/// <param name="ExpiresAt">When the code stops working (UTC); a text not sent by then is useless.</param>
public sealed record PhoneVerificationRequestedIntegrationEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    string PhoneNumber,
    string ProtectedCode,
    DateTimeOffset ExpiresAt) : IIntegrationEvent
{
    // The number and the code (even encrypted) never reach a log line.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", UserId = ").Append(UserId).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}
