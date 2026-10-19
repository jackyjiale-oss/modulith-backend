using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences.Update;

/// <summary>
/// Applies the caller's choices and answers with the preferences as <c>GET</c> reads them (the same query handler).
/// <list type="bullet">
/// <item>All or nothing: every item is checked before anything is staged, in request order, and the first invalid one is the answer
/// (an unknown type, a channel the type is not delivered through, switching off a mandatory channel). Items that a later duplicate
/// supersedes are checked too.</item>
/// <item>The last of two items for the same type and channel wins.</item>
/// <item>A row exists only for a choice that differs from the default (D10): switching a channel off adds or keeps its row, switching
/// it on again deletes the row, and switching on a channel that has no row stores nothing. Switching on a mandatory channel is a no-op.</item>
/// <item>Two requests of one user may create or delete the same row at once; <see cref="ConflictRetry"/> reads the rows again and
/// applies the request on top of what the other one did, so neither fails.</item>
/// </list>
/// </summary>
internal sealed class UpdatePreferencesCommandHandler(
    NotificationCatalog catalog,
    IPreferenceRepository preferences,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    IQueryHandler<GetPreferencesQuery, PreferencesResponse> getPreferences,
    TimeProvider timeProvider) : ICommandHandler<UpdatePreferencesCommand, PreferencesResponse>
{
    public async Task<Result<PreferencesResponse>> HandleAsync(UpdatePreferencesCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<PreferencesResponse>(UnauthenticatedCaller.Error);
        }

        var wanted = new Dictionary<(string TypeCode, NotificationChannel Channel), bool>();
        foreach (var item in command.Items)
        {
            var error = Check(item);
            if (error is not null)
            {
                return Result.Failure<PreferencesResponse>(error);
            }

            wanted[(item.TypeCode, item.Channel)] = item.Enabled;
        }

        var now = timeProvider.GetUtcNow();
        await ConflictRetry.SaveAsync(
            unitOfWork,
            async token => Stage(await preferences.ListAsync(userId, token), wanted, userId, now),
            cancellationToken);

        return await getPreferences.HandleAsync(new GetPreferencesQuery(), cancellationToken);
    }

    private Error? Check(ChannelPreferenceUpdate item)
    {
        var type = catalog.Find(item.TypeCode);
        if (type is null)
        {
            return PreferenceErrors.UnknownType(item.TypeCode);
        }

        if (!type.DefaultChannels.Contains(item.Channel))
        {
            return PreferenceErrors.ChannelNotSupported(type.Code, item.Channel.ToString());
        }

        return !item.Enabled && type.IsMandatory(item.Channel)
            ? PreferenceErrors.ChannelMandatory(type.Code, item.Channel.ToString())
            : null;
    }

    private void Stage(
        IReadOnlyList<UserPreference> stored,
        Dictionary<(string TypeCode, NotificationChannel Channel), bool> wanted,
        Guid userId,
        DateTimeOffset now)
    {
        foreach (var ((typeCode, channel), enabled) in wanted)
        {
            // Enabling a mandatory channel is a no-op: it is always on, and no row is ever stored for it.
            if (catalog.Find(typeCode)!.IsMandatory(channel))
            {
                continue;
            }

            var row = stored.FirstOrDefault(preference => preference.TypeCode == typeCode && preference.Channel == channel);
            if (enabled)
            {
                if (row is not null)
                {
                    preferences.Remove(row);
                }
            }
            else if (row is null)
            {
                preferences.Add(UserPreference.Create(userId, typeCode, channel, isEnabled: false, now));
            }
            else if (row.IsEnabled)
            {
                row.SetEnabled(false, now);
            }
        }
    }
}
