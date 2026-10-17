using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Messaging;

/// <summary>
/// Publishes an integration event to its handlers in the same process (ADR 0018): one after another, in registration order, each
/// created and run in its own async DI scope, so every consumer gets its own <c>DbContext</c> and a failure of one, including a failure
/// to create it, cannot stop or leak into another. Every handler runs even when an earlier one throws; afterwards one failure is
/// rethrown as it is and several as an <see cref="AggregateException"/>, so the outbox retries the publishing domain event handler and
/// the consumers that already succeeded skip the repeat through their inbox. Cancellation is rethrown at once, unwrapped.
/// </summary>
/// <remarks>
/// The handlers are the ones <c>AddApplicationHandlers</c> recorded as <see cref="IntegrationEventHandlerRegistration"/>s; each is
/// resolved as its own type, so a scope creates only the handler it runs.
/// </remarks>
internal sealed class InProcessIntegrationEventPublisher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IntegrationEventHandlerRegistration> registrations) : IIntegrationEventPublisher
{
    private readonly ILookup<Type, Type> _handlerTypes = registrations.ToLookup(
        registration => registration.EventType,
        registration => registration.HandlerType);

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var failures = new List<Exception>();
        foreach (var handlerType in _handlerTypes[typeof(TEvent)])
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                var handler = (IIntegrationEventHandler<TEvent>)scope.ServiceProvider.GetRequiredService(handlerType);
                await handler.HandleAsync(integrationEvent, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                $"{failures.Count} handlers of {typeof(TEvent).Name} {integrationEvent.Id} failed.",
                failures);
        }
    }
}
