using Microsoft.AspNetCore.Builder;

namespace TemplateName.Web.Common.Concurrency;

/// <summary>Marks an endpoint whose success responses carry an <c>ETag</c> (ADR 0038), for the OpenAPI document and the coverage test.</summary>
public sealed class EntityTagMetadata
{
    /// <summary>The one instance the marker needs.</summary>
    public static EntityTagMetadata Instance { get; } = new();

    private EntityTagMetadata()
    {
    }
}

/// <summary>Declares that an endpoint returns an <c>ETag</c>; an update that takes <c>If-Match</c> also declares its 412.</summary>
public static class EntityTagEndpointExtensions
{
    /// <summary>Adds <see cref="EntityTagMetadata"/>: the OpenAPI document then shows the <c>ETag</c> header on the 2xx responses.</summary>
    public static TBuilder ProducesEntityTag<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(EntityTagMetadata.Instance);
    }
}
