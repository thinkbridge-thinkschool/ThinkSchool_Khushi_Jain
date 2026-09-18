namespace DocBook.Api.Extensions;

public static class SecurityHeaderExtensions
{
    // The page and the two files it pulls in. Every other path here answers with JSON.
    private static readonly string[] PagePaths = ["/", "/index.html", "/app.css", "/app.js"];

    // JSON renders nothing and embeds nothing, so the routes that serve it get the narrowest policy.
    private const string ApiPolicy = "default-src 'none'; frame-ancestors 'none'";

    // The page loads its own script and stylesheet and nothing else, inline script included.
    private const string PagePolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";

    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = IsPage(context.Request.Path) ? PagePolicy : ApiPolicy;
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            // Nothing here is worth a shared cache holding, and some of it names a patient.
            headers["Cache-Control"] = "no-store";

            await next();
        });

        return app;
    }

    private static bool IsPage(PathString path) =>
        PagePaths.Any(page => path.Equals(page, StringComparison.OrdinalIgnoreCase));
}
