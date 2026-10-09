using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace TemplateName.Application.Common.Messaging;

public static class MessagingServiceCollectionExtensions
{
    private static readonly Type[] HandlerInterfaces =
    [
        typeof(ICommandHandler<>),
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
        typeof(IDomainEventHandler<>),
        typeof(IIntegrationEventHandler<>),
    ];

    /// <summary>Registers every handler and validator in <paramref name="assembly"/>, including internal ones, as scoped.</summary>
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableToAny(HandlerInterfaces), publicOnly: false)
                .As(handlerType => handlerType.GetInterfaces().Where(IsHandlerInterface))
                .WithScopedLifetime());

        return services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
    }

    /// <summary>
    /// Wraps the registered command and query handlers with validation, then logging, so logging is outermost.
    /// Call it once, last, after every module has registered its handlers.
    /// </summary>
    public static IServiceCollection AddApplicationDecorators(this IServiceCollection services)
    {
        services.TryDecorate(typeof(ICommandHandler<>), typeof(ValidationDecorator.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationDecorator.QueryHandler<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(LoggingDecorator.CommandHandler<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingDecorator.CommandHandler<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingDecorator.QueryHandler<,>));

        return services;
    }

    private static bool IsHandlerInterface(Type interfaceType)
        => interfaceType.IsGenericType && HandlerInterfaces.Contains(interfaceType.GetGenericTypeDefinition());
}
