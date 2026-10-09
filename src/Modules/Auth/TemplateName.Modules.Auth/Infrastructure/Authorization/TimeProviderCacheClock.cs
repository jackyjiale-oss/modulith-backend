using Microsoft.Extensions.Internal;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// The clock of the in-memory cache (<c>MemoryCacheOptions.Clock</c>), read from the application's <see cref="TimeProvider"/>.
/// <c>HybridCache</c> keeps its local entries in that cache, so the 30-second permission lifetime follows the same clock as the rest of
/// the application, and a test clock moves it too.
/// </summary>
internal sealed class TimeProviderCacheClock(TimeProvider timeProvider) : ISystemClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
