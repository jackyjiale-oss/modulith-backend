namespace TemplateName.SharedKernel;

/// <summary>
/// A fact one module tells other modules about. Implementations are <c>public sealed record {Noun}{PastTenseVerb}IntegrationEvent</c>
/// in the publishing module's <c>.Contracts</c> project. They are published in process and never stored, so they carry no version:
/// a breaking change is a new record type (ADR 0018).
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>
    /// Identifies the event and stays the same when it is published again: the outbox message id of the domain event it came from, so
    /// a consumer's inbox recognizes a retry. One domain event maps to at most one integration event.
    /// </summary>
    Guid Id { get; }

    /// <summary>When the domain event behind it happened (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
