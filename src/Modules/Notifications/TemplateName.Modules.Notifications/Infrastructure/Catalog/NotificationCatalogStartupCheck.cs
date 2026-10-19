using Microsoft.Extensions.Hosting;
using TemplateName.Modules.Notifications.Application.Catalog;

namespace TemplateName.Modules.Notifications.Infrastructure.Catalog;

/// <summary>Touches the <see cref="NotificationCatalog"/> when the host starts, so an invalid type declaration stops the start instead of failing the first notification.</summary>
internal sealed class NotificationCatalogStartupCheck(NotificationCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.All;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
