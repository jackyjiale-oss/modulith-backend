using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A link to confirm an email address was issued and must be sent to the user. Published once per issued link; a resend publishes
/// another one.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the link was issued (UTC).</param>
/// <param name="UserId">The user the link belongs to.</param>
/// <param name="Email">The address the link was issued for, which is where it must be sent (the user address when it was issued).</param>
/// <param name="ProtectedActionUrl">
/// The complete link, encrypted with <c>ISecretProtector</c>. It is single-use and secret: store it only as this ciphertext, decrypt it
/// only while rendering the message, and never log it or put it in a subject or an in-app text.
/// </param>
/// <param name="ExpiresAt">When the link stops working (UTC).</param>
public sealed record EmailVerificationRequestedIntegrationEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    string Email,
    string ProtectedActionUrl,
    DateTimeOffset ExpiresAt) : IIntegrationEvent;
