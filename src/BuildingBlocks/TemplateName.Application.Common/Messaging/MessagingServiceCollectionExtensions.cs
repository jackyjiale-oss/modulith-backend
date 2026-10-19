using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TemplateName.Application.Common.Messaging;

public static class MessagingServiceCollectionExtensions
{
    private static readonly Type[] HandlerInterfaces =
    [
        typeof(ICommandHandler<>),
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
        typeof(IDomainEventHandler<>),
    ];

    /// <summary>
    /// Registers every handler and validator in <paramref name="assembly"/>, including internal ones, as scoped. Command, query and
    /// domain event handlers are registered as their handler interfaces. Integration event handlers are registered as their own type,
    /// each with an <see cref="IntegrationEventHandlerRegistration"/> per event type, so the publisher can create one consumer without
    /// creating the others; scanning an assembly twice registers each of them once.
    /// </summary>
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(classes => classes.AssignableToAny(HandlerInterfaces), publicOnly: false)
                .As(handlerType => handlerType.GetInterfaces().Where(IsHandlerInterface))
                .WithScopedLifetime());

        var integrationEventHandlers = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))
                .Select(contract => (EventType: contract.GetGenericArguments()[0], HandlerType: type)));
        foreach (var (eventType, handlerType) in integrationEventHandlers)
        {
            services.AddIntegrationEventHandler(eventType, handlerType);
        }

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

    /// <summary>
    /// Registers <paramref name="handlerType"/> as itself (scoped) and records that it handles <paramref name="eventType"/>; a pair
    /// already recorded is not recorded again.
    /// </summary>
    internal static IServiceCollection AddIntegrationEventHandler(this IServiceCollection services, Type eventType, Type handlerType)
    {
        var registration = new IntegrationEventHandlerRegistration(eventType, handlerType);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IntegrationEventHandlerRegistration)
            && Equals(descriptor.ImplementationInstance, registration)))
        {
            services.TryAddScoped(handlerType);
            services.AddSingleton(registration);
        }

        return services;
    }

    private static bool IsHandlerInterface(Type interfaceType)
        => interfaceType.IsGenericType && HandlerInterfaces.Contains(interfaceType.GetGenericTypeDefinition());
}
