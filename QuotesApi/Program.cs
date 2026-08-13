using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuotesApi.Authorization;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Time;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

// Key Vault URI is not a secret, so it's safe to check in via appsettings.json.
// Everything it resolves (e.g. AzureMonitor:ConnectionString below) never
// touches source control. DefaultAzureCredential uses the App Service/VM
// managed identity in Azure and falls back to the developer's Azure CLI
// login locally.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

// Named source for the manual spans this app creates itself (see the
// refresh-token reuse handling below), separate from the spans that
// AddAspNetCoreInstrumentation()/AddHttpClientInstrumentation() create
// automatically. Registered as a singleton so it can be injected into
// endpoint handlers the same way the rest of this file does.
var activitySource = new ActivitySource("QuotesApi");
builder.Services.AddSingleton(activitySource);

var telemetryBuilder = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("QuotesApi"))
    .WithTracing(tracing => tracing
        .AddSource("QuotesApi")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

// Only wire up the Azure Monitor exporter when a connection string is actually
// configured (Key Vault in Azure, user-secrets locally). This keeps local dev,
// which only has the OTLP exporter above feeding Jaeger, free of export
// warnings for a destination that was never configured.
if (!string.IsNullOrWhiteSpace(builder.Configuration["AzureMonitor:ConnectionString"]))
{
    telemetryBuilder.UseAzureMonitor();
}

builder.Services.AddProblemDetails();

// The typed options binding below is what every downstream consumer (the
// login/refresh handlers, CreateAccessToken) actually injects via
// IOptions<JwtOptions>. ValidateOnStart forces the byte-length check to run
// during app startup rather than on first use, so a bad key fails the same
// way a missing one does -- immediately, not on the first login attempt.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(
        options => Encoding.UTF8.GetByteCount(options.Key) >= 32,
        "JWT key must be at least 256 bits.")
    .ValidateOnStart();

// AddJwtBearer below wires up authentication middleware itself, which
// happens before builder.Build() -- too early to resolve IOptions<JwtOptions>
// from the DI container. This one bootstrap read binds directly from
// configuration for that reason only.
var jwtBootstrapOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");

var jwtSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtBootstrapOptions.Key));

var entraOptions = builder.Configuration
    .GetSection("Entra")
    .Get<EntraOptions>()
    ?? throw new InvalidOperationException("Entra configuration is missing.");

if (string.IsNullOrWhiteSpace(entraOptions.TenantId) ||
    string.IsNullOrWhiteSpace(entraOptions.ClientId) ||
    string.IsNullOrWhiteSpace(entraOptions.Audience))
{
    throw new InvalidOperationException(
        "Entra configuration is incomplete. Set Entra:TenantId, Entra:ClientId " +
        "and Entra:Audience (see appsettings.Development.json).");
}

var entraAuthority = $"https://login.microsoftonline.com/{entraOptions.TenantId}/v2.0";

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "Smart";
        options.DefaultChallengeScheme = "Smart";
    })
    .AddJwtBearer("InternalJwt", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtBootstrapOptions.Issuer,

            ValidateAudience = true,
            ValidAudience = jwtBootstrapOptions.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtSigningKey,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddJwtBearer("Entra", options =>
    {
        // Authority drives OIDC discovery (signing keys), independent of the
        // explicit issuer/audience checks below. No client secret is needed
        // to validate bearer access tokens.
        options.Authority = entraAuthority;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = entraAuthority,

            ValidateAudience = true,
            ValidAudience = entraOptions.Audience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddPolicyScheme("Smart", "Smart", options =>
    {
        // Peek at the (still unvalidated) issuer claim only to route the
        // token to the handler that will actually validate it. This is
        // not authentication -- the chosen JwtBearer handler still
        // performs full signature/issuer/audience/lifetime validation.
        options.ForwardDefaultSelector = context =>
            AuthenticationSchemeSelector.Select(context.Request.Headers.Authorization.ToString());
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("can-edit-quotes", policy =>
        policy.RequireAssertion(context =>
        {
            var scope =
                context.User.FindFirst("scope")?.Value ??
                context.User.FindFirst("scp")?.Value;

            return scope?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("quotes.write", StringComparer.Ordinal) == true;
        }));
});

builder.Services.AddSingleton<IAuthorizationHandler, CanModifyOwnQuoteHandler>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<RefreshTokenEvaluator>();

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=quotes.db";

builder.Services.AddDbContext<QuotesDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();

var app = builder.Build();

// Every log line written while handling this request -- including the
// request-summary line below and anything the exception handler logs --
// shares this TraceId, so a single request's log lines can be filtered
// together. PushProperty must stay active for the whole downstream
// pipeline, so next() is awaited inside the using block rather than
// returned directly from it.
//
// This reads Activity.Current.TraceId rather than HttpContext.TraceIdentifier
// specifically so the value matches the trace ID OpenTelemetry exports for the
// same request (ASP.NET Core creates that Activity before user middleware
// runs, independent of whether OpenTelemetry is even wired up) -- that's what
// lets a trace in Jaeger and its log lines in the console be found by the
// same ID. TraceIdentifier is kept only as a fallback for requests that
// somehow have no Activity.
app.Use(async (context, next) =>
{
    var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    using (LogContext.PushProperty("TraceId", traceId))
    {
        await next();
    }
});

app.UseSerilogRequestLogging();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var logger = context.RequestServices
            .GetRequiredService<ILogger<Program>>();

        logger.LogError(
            context.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?
                .Error,
            "Unhandled API exception");

        await Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.")
            .ExecuteAsync(context);
    });
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<QuotesDbContext>();

    await db.Database.MigrateAsync();

    if (!await db.Users.AnyAsync())
    {
        db.Users.Add(new User
        {
            Email = "admin@example.com",
            PasswordHash =
                BCrypt.Net.BCrypt.HashPassword("P@ssword1")
        });

        await db.SaveChangesAsync();
    }
}

app.UseAuthentication();
app.UseAuthorization();

MapAuthEndpoints(app);
MapQuoteEndpoints(app);

app.Run();

static void MapQuoteEndpoints(WebApplication app)
{
    var group = app.MapGroup("/api/quotes");

    group.MapGet("/", async (
        int? page,
        int? size,
        IQuoteRepository repository,
        CancellationToken cancellationToken) =>
    {
        var currentPage = page.GetValueOrDefault(1);
        var pageSize = size.GetValueOrDefault(10);

        if (currentPage < 1 || pageSize is < 1 or > 100)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["page/size"] =
                    [
                        "Page must be >= 1 and size must be between 1 and 100."
                    ]
                });
        }

        var result = await repository.GetPagedAsync(
            currentPage,
            pageSize,
            cancellationToken);

        return Results.Ok(new
        {
            page = currentPage,
            size = pageSize,
            total = result.Total,
            items = result.Items
        });
    });

    group.MapGet("/{id:int}", async (
        int id,
        IQuoteRepository repository,
        CancellationToken cancellationToken) =>
    {
        var quote = await repository.GetByIdAsync(
            id,
            cancellationToken);

        return quote is null
            ? Results.NotFound()
            : Results.Ok(quote);
    });

    group.MapPost("/", async (
        CreateQuoteRequest request,
        ClaimsPrincipal user,
        IQuoteRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        Quote quote;

        try
        {
            quote = Quote.Create(
                request.Author,
                request.Text,
                user.GetSubjectId()!);
        }
        catch (QuoteDomainException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Quote validation failed",
                detail: ex.Message);
        }

        quote = await repository.AddAsync(
            quote,
            cancellationToken);

        logger.LogInformation(
            "Created quote {QuoteId}",
            quote.Id);

        return Results.Created(
            $"/api/quotes/{quote.Id}",
            quote);
    }).RequireAuthorization("can-edit-quotes");

    group.MapDelete("/{id:int}", async (
        int id,
        ClaimsPrincipal user,
        IQuoteRepository repository,
        IAuthorizationService authorizationService,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var quote = await repository.GetByIdAsync(id, cancellationToken);

        if (quote is null)
        {
            return Results.NotFound();
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(
            user,
            quote,
            new CanModifyOwnQuoteRequirement());

        if (!authorizationResult.Succeeded)
        {
            return Results.Forbid();
        }

        await repository.DeleteAsync(id, cancellationToken);

        logger.LogInformation(
            "Deleted quote {QuoteId}",
            id);

        return Results.NoContent();
    }).RequireAuthorization("can-edit-quotes");
}

static void MapAuthEndpoints(WebApplication app)
{
    var authGroup = app.MapGroup("/api/auth");

    // ---------------------------------------------------------
    // LOGIN
    // ---------------------------------------------------------

    authGroup.MapPost("/login", async (
        LoginRequest request,
        QuotesDbContext db,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken) =>
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Email == request.Email,
                cancellationToken);

        if (user is null ||
            !BCrypt.Net.BCrypt.Verify(
                request.Password,
                user.PasswordHash))
        {
            return Results.Unauthorized();
        }

        var accessToken =
            CreateAccessToken(user, jwtOptions.Value);

        var refreshToken =
            GenerateRefreshToken();

        var refreshTokenEntity = new RefreshToken
        {
            TokenHash =
                HashRefreshToken(refreshToken),

            UserId = user.Id,

            ExpiresAt =
                DateTimeOffset.UtcNow.AddDays(7),

            FamilyId = Guid.NewGuid()
        };

        db.RefreshTokens.Add(refreshTokenEntity);

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in =
                jwtOptions.Value.AccessTokenMinutes * 60
        });
    });

    // ---------------------------------------------------------
    // REFRESH TOKEN ROTATION
    // ---------------------------------------------------------

    authGroup.MapPost("/refresh", async (
        RefreshTokenRequest request,
        QuotesDbContext db,
        IOptions<JwtOptions> jwtOptions,
        RefreshTokenEvaluator refreshTokenEvaluator,
        ActivitySource activitySource,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var tokenHash =
            HashRefreshToken(request.RefreshToken);

        var storedToken = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash,
                cancellationToken);

        // Token doesn't exist.
        if (storedToken is null)
        {
            return Results.Unauthorized();
        }

        var validation = refreshTokenEvaluator.Evaluate(storedToken);

        // -----------------------------------------------------
        // REUSE DETECTION
        // -----------------------------------------------------

        if (validation is RefreshTokenValidation.Reused)
        {
            // Revoking a whole token family is a multi-step, security-sensitive
            // operation on its own -- automatic instrumentation would only ever
            // show it as a handful of disconnected EF query spans, with no span
            // tying them together as "one family got revoked". This is exactly
            // the kind of non-trivial operation that gets its own manual span.
            using var activity = activitySource.StartActivity("revoke-refresh-token-family");
            activity?.SetTag("user.id", storedToken.UserId);
            activity?.SetTag("family.id", storedToken.FamilyId);

            logger.LogWarning(
                "Refresh token reuse detected for user {UserId} and family {FamilyId}",
                storedToken.UserId,
                storedToken.FamilyId);

            var familyTokens = await db.RefreshTokens
                .Where(t =>
                    t.FamilyId == storedToken.FamilyId)
                .ToListAsync(cancellationToken);

            activity?.SetTag("family.token_count", familyTokens.Count);

            foreach (var token in familyTokens)
            {
                token.RevokedAt ??=
                    DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(
                cancellationToken);

            return Results.Unauthorized();
        }

        // -----------------------------------------------------
        // EXPIRATION CHECK
        // -----------------------------------------------------

        if (validation is RefreshTokenValidation.Expired)
        {
            return Results.Unauthorized();
        }

        // -----------------------------------------------------
        // ROTATE REFRESH TOKEN
        // -----------------------------------------------------

        var newRefreshToken =
            GenerateRefreshToken();

        var newRefreshTokenHash =
            HashRefreshToken(newRefreshToken);

        // Revoke old token.
        storedToken.RevokedAt =
            DateTimeOffset.UtcNow;

        storedToken.ReplacedByTokenHash =
            newRefreshTokenHash;

        // Create replacement token in same family.
        var replacement = new RefreshToken
        {
            TokenHash =
                newRefreshTokenHash,

            UserId =
                storedToken.UserId,

            ExpiresAt =
                DateTimeOffset.UtcNow.AddDays(7),

            FamilyId =
                storedToken.FamilyId
        };

        db.RefreshTokens.Add(replacement);

        // Create new access token.
        var accessToken =
            CreateAccessToken(
                storedToken.User,
                jwtOptions.Value);

        await db.SaveChangesAsync(
            cancellationToken);

        return Results.Ok(new
        {
            access_token = accessToken,
            refresh_token = newRefreshToken,
            expires_in =
                jwtOptions.Value.AccessTokenMinutes * 60
        });
    });

    // ---------------------------------------------------------
    // LOGOUT
    // ---------------------------------------------------------

    authGroup.MapPost("/logout", async (
        RefreshTokenRequest request,
        QuotesDbContext db,
        CancellationToken cancellationToken) =>
    {
        var tokenHash =
            HashRefreshToken(request.RefreshToken);

        var storedToken = await db.RefreshTokens
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash,
                cancellationToken);

        // Logout is idempotent.
        if (storedToken is null)
        {
            return Results.NoContent();
        }

        storedToken.RevokedAt ??=
            DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(
            cancellationToken);

        return Results.NoContent();
    });
}

// =============================================================
// JWT ACCESS TOKEN
// =============================================================

static string CreateAccessToken(
    User user,
    JwtOptions jwtOptions)
{
    var claims = new[]
    {
        new Claim(
            JwtRegisteredClaimNames.Sub,
            user.Email),

        new Claim(
            JwtRegisteredClaimNames.Jti,
            Guid.NewGuid().ToString()),

        new Claim(
            JwtRegisteredClaimNames.Iss,
            jwtOptions.Issuer),

        new Claim(
            JwtRegisteredClaimNames.Aud,
            jwtOptions.Audience),

        new Claim("scope", "quotes.write")
    };

    var token = new JwtSecurityToken(
        issuer: jwtOptions.Issuer,
        audience: jwtOptions.Audience,
        claims: claims,
        expires:
            DateTime.UtcNow.AddMinutes(
                jwtOptions.AccessTokenMinutes),
        signingCredentials:
            new SigningCredentials(
                new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(
                        jwtOptions.Key)),
                SecurityAlgorithms.HmacSha256));

    return new JwtSecurityTokenHandler()
        .WriteToken(token);
}

// =============================================================
// REFRESH TOKEN GENERATION
// =============================================================

static string GenerateRefreshToken()
{
    return Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(64));
}

// =============================================================
// REFRESH TOKEN HASHING
// =============================================================

static string HashRefreshToken(string token)
{
    var hash = SHA256.HashData(
        Encoding.UTF8.GetBytes(token));

    return Convert.ToHexString(hash);
}