namespace DocBook.Api.Extensions;

public static class SecurityHeaderExtensions
{
    // A JSON API renders nothing and embeds nothing, so the policy it sends is the narrowest one.
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            // Nothing here is worth a shared cache holding, and some of it names a patient.
            headers["Cache-Control"] = "no-store";

            await next();
        });

        return app;
    }
}
