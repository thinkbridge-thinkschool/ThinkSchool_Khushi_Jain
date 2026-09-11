using Microsoft.OpenApi;

namespace DocBook.Api.Extensions;

public static class OpenApiExtensions
{
    public const string DocumentName = "v1";

    public static WebApplicationBuilder AddApiDocument(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "DocBook",
                    Version = DocumentName,
                    Description = "Clinic appointment booking. Every route but /health needs a bearer token."
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

                // Stated in the contract rather than left for a caller to discover by being refused.
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "An access token from POST /api/v1/tokens."
                };

                document.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("bearer", document)] = []
                    }
                ];

                return Task.CompletedTask;
            });
        });

        return builder;
    }
}
