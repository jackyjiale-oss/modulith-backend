using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences.UpdateQuietHours;

/// <summary>
/// Stores or clears the caller's window and answers with it as <c>GET</c> reads it (the same query handler, so the time zone is Auth's).
/// The rules live here and not in a validator because they answer with their own code, <c>notifications.invalid_quiet_hours</c>: the
/// two times are both set or both null, and a window must not start where it ends (<see cref="QuietHours.Create"/>). The profile row
/// is created on the first window; clearing when there is no row creates none. A window that crosses midnight (22:00 to 07:00) is
/// valid. Two requests that create the row or change it at once are settled by <see cref="ConflictRetry"/> (the last one wins).
/// </summary>
internal sealed class UpdateQuietHoursCommandHandler(
    IPreferenceRepository preferences,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    IQueryHandler<GetQuietHoursQuery, QuietHoursResponse> getQuietHours,
    TimeProvider timeProvider) : ICommandHandler<UpdateQuietHoursCommand, QuietHoursResponse>
{
    public async Task<Result<QuietHoursResponse>> HandleAsync(UpdateQuietHoursCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<QuietHoursResponse>(UnauthenticatedCaller.Error);
        }

        QuietHours? window = null;
        if (command.Start is { } start && command.End is { } end)
        {
            var created = QuietHours.Create(start, end);
            if (created.IsFailure)
            {
                return Result.Failure<QuietHoursResponse>(created.Error);
            }

            window = created.Value;
        }
        else if (command.Start is not null || command.End is not null)
        {
            return Result.Failure<QuietHoursResponse>(PreferenceErrors.InvalidQuietHours);
        }

        var now = timeProvider.GetUtcNow();
        await ConflictRetry.SaveAsync(unitOfWork, token => StageAsync(userId, window, now, token), cancellationToken);

        return await getQuietHours.HandleAsync(new GetQuietHoursQuery(), cancellationToken);
    }

    private async Task StageAsync(Guid userId, QuietHours? window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var profile = await preferences.GetSettingsAsync(userId, cancellationToken);
        if (profile is null)
        {
            if (window is null)
            {
                return;
            }

            profile = UserNotificationProfile.Create(userId, now);
            preferences.Add(profile);
        }

        profile.SetQuietHours(window, now);
    }
}
