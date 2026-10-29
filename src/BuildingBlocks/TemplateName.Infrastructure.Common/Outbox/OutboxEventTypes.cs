using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TemplateName.SharedKernel;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// The domain event types the outbox of <typeparamref name="TContext"/> can dispatch, keyed by <see cref="Type.FullName"/> (the value
/// stored in <c>OutboxMessages.Type</c>). <c>AddOutbox</c> builds it from the module's assembly, and it always holds
/// <see cref="EntityChangesRecordedDomainEvent"/>, which the audit trail interceptor writes to every module's outbox.
/// </summary>
/// <remarks>
/// A row whose type is missing here fails to dispatch, so the stored names are kept stable like a published contract: renaming an
/// event type, the change set record included, orphans its pending rows.
/// </remarks>
public sealed class OutboxEventTypes<TContext>
    where TContext : DbContext
{
    private readonly Dictionary<string, Type> _types;

    internal OutboxEventTypes(Assembly domainEventsAssembly)
    {
        _types = domainEventsAssembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false, FullName: not null }
                && typeof(IDomainEvent).IsAssignableFrom(type))
            .ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        _types.TryAdd(typeof(EntityChangesRecordedDomainEvent).FullName!, typeof(EntityChangesRecordedDomainEvent));
    }

    internal Type? Find(string typeName) => _types.GetValueOrDefault(typeName);
}
