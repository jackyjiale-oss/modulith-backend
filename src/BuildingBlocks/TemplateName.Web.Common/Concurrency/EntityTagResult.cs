using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Concurrency;

namespace TemplateName.Web.Common.Concurrency;

/// <summary>Runs a result after setting the <c>ETag</c> response header (ADR 0038). Use it on success results only.</summary>
public static class EntityTagResult
{
    /// <summary>Wraps <paramref name="result"/> so that its response carries <paramref name="tag"/> as <c>ETag</c>.</summary>
    public static IResult WithEntityTag(this IResult result, EntityTag tag)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new TaggedResult(result, tag);
    }

    private sealed class TaggedResult(IResult inner, EntityTag tag) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.ETag = tag.ToHeaderValue();
            return inner.ExecuteAsync(httpContext);
        }
    }
}
