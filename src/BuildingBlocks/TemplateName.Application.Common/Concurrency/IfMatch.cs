namespace TemplateName.Application.Common.Concurrency;

/// <summary>
/// The precondition a command carries from an <c>If-Match</c> header (ADR 0038): <see cref="None"/> when the header is absent,
/// <see cref="Any"/> for <c>*</c>, or a set of strong tags (<see cref="Of"/>). A weak tag never matches, so a header of weak tags only
/// becomes an empty set, which no version satisfies.
/// </summary>
public sealed class IfMatch
{
    private readonly IReadOnlySet<EntityTag>? _tags;

    private IfMatch(IReadOnlySet<EntityTag>? tags) => _tags = tags;

    /// <summary>No <c>If-Match</c> header: no precondition, today's behaviour.</summary>
    public static IfMatch None { get; } = new(null);

    /// <summary><c>If-Match: *</c>: satisfied while the resource exists, which every resource that takes it does.</summary>
    public static IfMatch Any { get; } = new(null);

    /// <summary>A list of strong tags, satisfied when the current version is one of them; an empty list is never satisfied.</summary>
    public static IfMatch Of(IReadOnlyCollection<EntityTag> strongTags)
    {
        ArgumentNullException.ThrowIfNull(strongTags);
        return new IfMatch(strongTags.ToHashSet());
    }

    /// <summary>Whether the resource's current version meets the precondition.</summary>
    public bool IsSatisfiedBy(EntityTag current) => _tags is null || _tags.Contains(current);
}
