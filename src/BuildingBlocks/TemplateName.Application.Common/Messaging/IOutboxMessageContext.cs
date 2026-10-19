namespace TemplateName.Application.Common.Messaging;

/// <summary>
/// The outbox message a domain event handler is running for. The outbox dispatcher sets it in each message's DI scope before running
/// the handlers, so it is the same on every retry of the message; use <see cref="MessageId"/> as the <c>Id</c> of the integration event
/// a handler publishes (ADR 0018). Reading it anywhere else (a request, a background job, an integration event handler, which runs in
/// a scope of its own) throws <see cref="InvalidOperationException"/>.
/// </summary>
public interface IOutboxMessageContext
{
    /// <summary>The <c>OutboxMessages.Id</c> of the message being dispatched.</summary>
    Guid MessageId { get; }

    /// <summary>When the domain event in the message happened (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
