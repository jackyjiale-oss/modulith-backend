using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TemplateName.Infrastructure.Common.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox of <typeparamref name="TContext"/>: the event types it can dispatch (every <c>IDomainEvent</c> in
    /// <paramref name="domainEventsAssembly"/>), a singleton <see cref="OutboxDispatcher{TContext}"/> and the background service
    /// that polls it while <c>Outbox:Enabled</c> is set. The context must call <c>ApplyOutbox()</c> and be registered with
    /// <c>AddModuleDbContext</c>; the handlers come from <c>AddApplicationHandlers</c>, and the scoped <c>IOutboxMessageContext</c> the
    /// dispatcher sets for them from <c>AddInfrastructureCommon</c>.
    /// </summary>
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services, Assembly domainEventsAssembly)
        where TContext : DbContext
    {
        services.AddSingleton(new OutboxEventTypes<TContext>(domainEventsAssembly));
        services.AddSingleton<OutboxDispatcher<TContext>>();
        services.AddHostedService<OutboxBackgroundService<TContext>>();

        return services;
    }
}
