using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Messaging;

/// <summary>
/// Publishes an integration event to its handlers in the same process (ADR 0018): one after another, in registration order, each in
/// its own async DI scope, so every consumer gets its own <c>DbContext</c> and a failed save of one cannot leak into another. Every
/// handler runs even when an earlier one throws; afterwards one failure is rethrown as it is and several as an
/// <see cref="AggregateException"/>, so the outbox retries the publishing domain event handler and the consumers that already succeeded
/// skip the repeat through their inbox. Cancellation is rethrown at once, unwrapped.
/// </summary>
/// <remarks>
/// DI cannot resolve a single implementation out of several registrations, so each scope resolves the whole handler list and runs the
/// handler at its position. Handler constructors therefore run once per handler and scope; like any DI constructor they must be cheap
/// and free of side effects.
/// </remarks>
internal sealed class InProcessIntegrationEventPublisher(IServiceScopeFactory scopeFactory) : IIntegrationEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var failures = new List<Exception>();
        for (var index = 0; ; index++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handlers = scope.ServiceProvider.GetServices<IIntegrationEventHandler<TEvent>>().ToArray();

            if (index < handlers.Length)
            {
                try
                {
                    await handlers[index].HandleAsync(integrationEvent, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures.Add(exception);
                }
            }

            if (index >= handlers.Length - 1)
            {
                break;
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
