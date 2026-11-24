using System.Text;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A code to sign in with the account's verified phone number was issued; the consuming module texts it to <see cref="PhoneNumber"/>.
/// A project without such a module keeps <c>Auth:Phone:SignInEnabled</c> off.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the code was issued (UTC).</param>
/// <param name="UserId">The account the code signs in.</param>
/// <param name="PhoneNumber">The account's verified number in E.164, where the text must go.</param>
/// <param name="ProtectedCode">The 6-digit code encrypted with <c>ISecretProtector</c>, with the same ciphertext rules as a link.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
public sealed record PhoneSignInCodeRequestedIntegrationEvent(
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
