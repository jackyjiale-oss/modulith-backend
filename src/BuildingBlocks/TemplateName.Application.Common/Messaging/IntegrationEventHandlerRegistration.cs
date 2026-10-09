namespace TemplateName.Application.Common.Messaging;

/// <summary>
/// Says that <see cref="HandlerType"/> (registered as itself, scoped) handles <see cref="EventType"/>. <c>AddApplicationHandlers</c>
/// adds one singleton per handler and event type; the integration event publisher reads them to create each handler on its own, in its
/// own scope (ADR 0018).
/// </summary>
public sealed record IntegrationEventHandlerRegistration(Type EventType, Type HandlerType);
