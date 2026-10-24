using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.Infrastructure.Common.Inbox;

public static class InboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="Inbox{TContext}"/> as scoped, so it shares the consumer's scoped <typeparamref name="TContext"/>, and the
    /// recurring job <c>{Context}InboxCleanup</c> that deletes the rows processed more than <c>Retention:Inbox</c> ago. The context must
    /// call <c>ApplyInbox()</c> and be registered with <c>AddModuleDbContext</c>.
    /// </summary>
    public static IServiceCollection AddInbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<Inbox<TContext>>();
        services.AddRecurringJob<InboxCleanupJob<TContext>>();

        return services;
    }
}
