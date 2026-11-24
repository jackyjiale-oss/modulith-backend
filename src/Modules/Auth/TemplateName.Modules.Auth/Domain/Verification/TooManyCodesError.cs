using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>
/// A phone code was refused by the resend cooldown or the daily cap. The endpoint answers it like the host's rate limiter: 429
/// <c>rate_limit.exceeded</c> with <c>Retry-After</c> set from <see cref="RetryAfter"/> (whole seconds, rounded up).
/// </summary>
/// <param name="RetryAfter">How long until the next code is allowed.</param>
internal sealed record TooManyCodesError(TimeSpan RetryAfter)
    : Error("rate_limit.exceeded", "Too many requests. Try again later.", ErrorType.Failure);
