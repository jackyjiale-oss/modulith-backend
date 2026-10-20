using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.Infrastructure.Common.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox of <typeparamref name="TContext"/>: the event types it can dispatch (every <c>IDomainEvent</c> in
    /// <paramref name="domainEventsAssembly"/>), a singleton <see cref="OutboxDispatcher{TContext}"/> and the background service
    /// that polls it while <c>Outbox:Enabled</c> is set, and the recurring job <c>{Context}OutboxCleanup</c> that deletes processed messages
    /// after <c>Retention:Outbox</c> and reports the abandoned ones (<c>platform.outbox.abandoned</c>). The context must call <c>ApplyOutbox()</c> and be registered with
    /// <c>AddModuleDbContext</c>; the handlers come from <c>AddApplicationHandlers</c>, and the scoped <c>IOutboxMessageContext</c> the
    /// dispatcher sets for them from <c>AddInfrastructureCommon</c>.
    /// </summary>
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services, Assembly domainEventsAssembly)
        where TContext : DbContext
    {
        services.AddSingleton(new OutboxEventTypes<TContext>(domainEventsAssembly));
        services.AddSingleton<OutboxDispatcher<TContext>>();
        services.AddHostedService<OutboxBackgroundService<TContext>>();
        services.TryAddSingleton<OutboxAbandonedGauge>();
        services.AddRecurringJob<OutboxCleanupJob<TContext>>();

        return services;
    }
}
