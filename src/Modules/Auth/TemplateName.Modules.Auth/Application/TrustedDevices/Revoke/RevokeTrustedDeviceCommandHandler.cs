using System.Text.Json;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.TrustedDevices;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.TrustedDevices.Revoke;

/// <summary>
/// Revokes one of the caller's trusted devices, so its token gets the challenge again at the next sign-in. A device that does not exist
/// and a device of another user answer with the same <see cref="TrustedDeviceErrors.NotFound"/>, so ids cannot be probed. A device of the
/// caller that is already revoked succeeds without changing anything (the first revocation time stays, and no second audit row is
/// written). That check is a read, not a conditional update, so two revocations racing each other can both pass it and both write an
/// audit row; the stored revocation time is then the later save's. An expired device, or one an older security stamp already made
/// unusable, is revoked like any other. No step-up: reducing trust is always safe (plan decision D11).
/// </summary>
internal sealed class RevokeTrustedDeviceCommandHandler(
    ITrustedDeviceRepository trustedDevices,
    ICurrentUser currentUser,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<RevokeTrustedDeviceCommand>
{
    public async Task<Result> HandleAsync(RevokeTrustedDeviceCommand command, CancellationToken cancellationToken)
    {
        var device = currentUser.UserId is { } userId
            ? await trustedDevices.GetForUserAsync(command.TrustedDeviceId, userId, cancellationToken)
            : null;
        if (device is null)
        {
            return Result.Failure(TrustedDeviceErrors.NotFound(command.TrustedDeviceId));
        }

        if (device.RevokedAt is not null)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow();
        device.Revoke(now);
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.TrustedDeviceRevoked,
            succeeded: true,
            now,
            userId: device.UserId,
            sessionId: currentUser.SessionId,
            details: JsonSerializer.Serialize(new { trustedDeviceId = device.Id })));

        // A revocation is stored even when the client goes away; the device has no concurrency token, so the save cannot conflict.
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        return Result.Success();
    }
}
