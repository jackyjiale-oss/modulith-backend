using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Data;
using TemplateName.Modules.Auth.Infrastructure.Authorization;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>Permission checkers whose database reads a test can count.</summary>
internal static class PermissionCheckers
{
    /// <summary>
    /// A <see cref="PermissionChecker"/> over the host's own <see cref="HybridCache"/> (and so its local entries and, with Redis, its
    /// distributed cache), whose connections are counted by the returned <see cref="RecordingDbConnectionFactory"/>.
    /// </summary>
    internal static (PermissionChecker Checker, RecordingDbConnectionFactory Reads) Recording(IServiceProvider services)
    {
        var reads = new RecordingDbConnectionFactory(services.GetRequiredService<IDbConnectionFactory>());
        return (new PermissionChecker(reads, services.GetRequiredService<HybridCache>()), reads);
    }
}
