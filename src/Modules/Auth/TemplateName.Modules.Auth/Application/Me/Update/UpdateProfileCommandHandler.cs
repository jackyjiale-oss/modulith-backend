using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Me.GetMe;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Me.Update;

/// <summary>
/// Saves the caller's display name, language and time zone and answers with the profile as <c>GET me</c> reads it (the same query
/// handler, so roles and permissions are included). The locale is stored in its canonical spelling and the display name trimmed. A
/// caller whose account is gone or suspended gets <see cref="UserErrors.NotFound"/>, which the endpoint answers like a request without
/// a valid token. The language applies to the next access token the session issues (a refresh), because the <c>locale</c> claim is
/// read from the token and not looked up per request.
/// </summary>
internal sealed class UpdateProfileCommandHandler(
    IUserRepository users,
    ICurrentUser currentUser,
    IQueryHandler<GetMeQuery, MeResponse> getMe,
    IAuthAuditWriter auditWriter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<UpdateProfileCommand, MeResponse>
{
    public async Task<Result<MeResponse>> HandleAsync(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<MeResponse>(UserErrors.NotFound(Guid.Empty));
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return Result.Failure<MeResponse>(UserErrors.NotFound(userId));
        }

        // The validator has accepted the locale before the handler runs, so this cannot fail.
        UpdateProfileCommandValidator.TryGetCanonicalLocale(command.Locale, out var locale);
        user.UpdateProfile(command.DisplayName.Trim(), locale, command.TimeZone);
        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.ProfileUpdated, succeeded: true, timeProvider.GetUtcNow(), userId: userId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await getMe.HandleAsync(new GetMeQuery(), cancellationToken);
    }
}
