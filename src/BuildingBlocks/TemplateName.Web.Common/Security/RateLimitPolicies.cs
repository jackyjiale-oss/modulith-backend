namespace TemplateName.Web.Common.Security;

/// <summary>The named rate-limit policies <c>AddHttpSecurity</c> registers, for <c>RequireRateLimiting(name)</c> on an endpoint or group.</summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// A fixed window per client address (<c>ip:{address}</c>, as left by the forwarded-headers middleware), by default 10 requests per
    /// minute (<c>RateLimiting:AuthStrictPermitLimit</c>, <c>RateLimiting:AuthStrictWindow</c>). For every anonymous credential endpoint
    /// (login, register, forgot password, resend confirmation, …). It applies on top of the global limiter; a rejection is the usual
    /// 429 <c>rate_limit.exceeded</c> problem with <c>Retry-After</c>.
    /// </summary>
    public const string AuthStrict = "auth-strict";
}
