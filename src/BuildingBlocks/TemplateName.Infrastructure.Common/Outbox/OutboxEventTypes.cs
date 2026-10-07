using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// The domain event types the outbox of <typeparamref name="TContext"/> can dispatch, keyed by <see cref="Type.FullName"/> (the value
/// stored in <c>OutboxMessages.Type</c>). <c>AddOutbox</c> builds it from the module's assembly.
/// </summary>
public sealed class OutboxEventTypes<TContext>
    where TContext : DbContext
{
    private readonly Dictionary<string, Type> _types;

    internal OutboxEventTypes(Assembly domainEventsAssembly)
        => _types = domainEventsAssembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false, FullName: not null }
                && typeof(IDomainEvent).IsAssignableFrom(type))
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);

    internal Type? Find(string typeName) => _types.GetValueOrDefault(typeName);
}
