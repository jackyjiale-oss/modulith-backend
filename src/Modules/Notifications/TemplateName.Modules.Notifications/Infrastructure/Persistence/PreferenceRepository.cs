using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.Modules.Notifications.Infrastructure.Persistence;

internal sealed class PreferenceRepository(NotificationsDbContext context) : IPreferenceRepository
{
    public async Task<IReadOnlyList<UserPreference>> ListAsync(Guid userId, CancellationToken cancellationToken)
        => await context.Set<UserPreference>()
            .Where(preference => preference.UserId == userId)
            .OrderBy(preference => preference.TypeCode)
            .ThenBy(preference => preference.Channel)
            .ToListAsync(cancellationToken);

    public Task<UserNotificationProfile?> GetSettingsAsync(Guid userId, CancellationToken cancellationToken)
        => context.Set<UserNotificationProfile>().SingleOrDefaultAsync(profile => profile.Id == userId, cancellationToken);

    public void Add(UserPreference preference) => context.Set<UserPreference>().Add(preference);

    public void Add(UserNotificationProfile profile) => context.Set<UserNotificationProfile>().Add(profile);
}
