using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>Outbox dispatch (section <c>Outbox</c>), shared by every module's dispatcher.</summary>
public sealed class OutboxOptions
{
    internal const string SectionName = "Outbox";

    /// <summary>Runs the background dispatchers. Tests turn it off and call <c>ProcessBatchAsync</c> themselves.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The pause between polls once the pending messages are drained.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most messages one dispatcher claims per batch.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Failed attempts after which a message is abandoned (left unprocessed with its last error).</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed batch stays hidden from other dispatchers. Keep it longer than the slowest batch takes.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}
