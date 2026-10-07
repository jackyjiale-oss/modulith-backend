using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary><c>Idempotency-Key</c> handling (section <c>Idempotency</c>).</summary>
public sealed class IdempotencyOptions : IValidatableObject
{
    internal const string SectionName = "Idempotency";

    /// <summary>How long a key and its stored response are kept. A request that reuses an expired key runs again.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a key stays leased to the request that is running with it. After that the key is treated as abandoned (the process
    /// died, or the response could not be stored) and the next request with it runs the endpoint again. Keep it longer than the
    /// slowest idempotent request; it may not exceed <see cref="TimeToLive"/>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan InProgressTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (InProgressTimeout > TimeToLive)
        {
            yield return new ValidationResult(
                $"{nameof(InProgressTimeout)} may not be longer than {nameof(TimeToLive)}.",
                [nameof(InProgressTimeout)]);
        }
    }
}
