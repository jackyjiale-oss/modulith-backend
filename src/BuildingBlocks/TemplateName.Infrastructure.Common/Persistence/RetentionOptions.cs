using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// How long the building blocks keep what they own before a recurring cleanup job deletes it (section <c>Retention</c>, ADR 0028). A row
/// goes only when it is strictly older than the retention; a retention of <see langword="null"/> or zero turns that cleanup off.
/// </summary>
public sealed class RetentionOptions : IValidatableObject
{
    internal const string SectionName = "Retention";

    /// <summary>The shortest inbox retention that is not off: the outbox retries a message for about an hour, so a day outlasts any redelivery.</summary>
    internal static readonly TimeSpan MinimumInbox = TimeSpan.FromDays(1);

    /// <summary>The most rows one delete statement removes, so a transaction and its locks stay small; a run repeats it up to <c>Jobs:MaxBatchesPerRun</c> times.</summary>
    [Range(1, 10000)]
    public int BatchSize { get; set; } = 1000;

    /// <summary>How long a published outbox message (and its consumer rows) is kept after <c>ProcessedAt</c>. Abandoned messages are never deleted.</summary>
    public TimeSpan? Outbox { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long an inbox row is kept after <c>ProcessedAt</c>. It must outlast outbox redelivery, so a value between zero and one day is
    /// rejected.
    /// </summary>
    public TimeSpan? Inbox { get; set; } = TimeSpan.FromDays(7);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Outbox is { } outbox && outbox < TimeSpan.Zero)
        {
            yield return new ValidationResult($"{nameof(Outbox)} may not be negative; use zero to turn the cleanup off.", [nameof(Outbox)]);
        }

        if (Inbox is { } inbox)
        {
            if (inbox < TimeSpan.Zero)
            {
                yield return new ValidationResult($"{nameof(Inbox)} may not be negative; use zero to turn the cleanup off.", [nameof(Inbox)]);
            }
            else if (inbox > TimeSpan.Zero && inbox < MinimumInbox)
            {
                yield return new ValidationResult(
                    $"{nameof(Inbox)} may not be shorter than {MinimumInbox.TotalDays} day: a redelivered message must still meet its inbox row. Use zero to turn the cleanup off.",
                    [nameof(Inbox)]);
            }
        }
    }
}
