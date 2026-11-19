using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace TemplateName.Web.Common.Concurrency;

/// <summary>Documents the <c>ETag</c> response header of endpoints marked with <see cref="EntityTagMetadata"/> (ADR 0038).</summary>
public static class EntityTagOpenApi
{
    private const string Description = "The version of the resource's editable state; send it back as If-Match to update it safely.";

    /// <summary>Adds the operation transformer. The <c>If-Match</c> request header needs none: endpoints bind it with <c>[FromHeader]</c>.</summary>
    public static OpenApiOptions AddEntityTagHeaders(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddOperationTransformer((operation, context, _) =>
        {
            if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<EntityTagMetadata>().Any() || operation.Responses is null)
            {
                return Task.CompletedTask;
            }

            foreach (var (status, response) in operation.Responses)
            {
                if (status.StartsWith('2') && response is OpenApiResponse concrete)
                {
                    concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    concrete.Headers[HeaderNames.ETag] = new OpenApiHeader
                    {
                        Description = Description,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                }
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
