using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary><c>Idempotency-Key</c> handling (section <c>Idempotency</c>).</summary>
public sealed class IdempotencyOptions
{
    internal const string SectionName = "Idempotency";

    /// <summary>How long a key and its stored response are kept. A request that reuses an expired key runs again.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);
}
