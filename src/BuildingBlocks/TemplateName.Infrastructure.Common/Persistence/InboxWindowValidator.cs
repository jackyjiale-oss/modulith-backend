using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Outbox;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Checks on start that <c>Retention:Inbox</c> is longer than the time the outbox retries a message, which <c>Outbox:MaxAttempts</c> sets
/// (<see cref="OutboxRetryPolicy.RetryWindow"/>). An inbox row deleted before a redelivery would let a consumer run twice (ADR 0028). An
/// inbox retention of zero or <see langword="null"/> (the cleanup is off) always passes.
/// </summary>
internal sealed class InboxWindowValidator(IOptions<OutboxOptions> outbox) : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        if (options.Inbox is not { } inbox || inbox <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Success;
        }

        var maxAttempts = outbox.Value.MaxAttempts;
        var window = OutboxRetryPolicy.RetryWindow(maxAttempts);
        return inbox > window
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Retention:Inbox ({inbox}) must be longer than the time the outbox retries a message, {window} with Outbox:MaxAttempts = {maxAttempts}: " +
                "an inbox row deleted before a redelivery lets a consumer run twice. Raise Retention:Inbox or lower Outbox:MaxAttempts.");
    }
}
