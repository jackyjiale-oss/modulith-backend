using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TemplateName.Web.Common.Security;

public static class OpenApiSecurity
{
    /// <summary>The name of the security scheme in the document's <c>components.securitySchemes</c>.</summary>
    public const string SchemeName = "Bearer";

    /// <summary>
    /// Declares the access token in the OpenAPI document: a <c>Bearer</c> security scheme (<c>http</c>, <c>bearer</c>, format <c>JWT</c>),
    /// required by every operation whose endpoint carries authorization data (<see cref="IAuthorizeData"/>, for example
    /// <c>RequirePermission</c> or <c>RequireAuthorization</c>) and no <see cref="IAllowAnonymous"/>. An endpoint protected only by the
    /// fallback policy carries no such data and is not marked.
    /// </summary>
    public static OpenApiOptions AddBearerSecurity(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "An access token from the Auth module, sent as Authorization: Bearer {token}.",
            };

            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [],
                });
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
