using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
using QuotesApi.Contracts;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

var jwtSettings = builder.Configuration
    .GetSection("Jwt")
    .Get<JwtSettings>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");

var keyBytes = Encoding.UTF8.GetBytes(jwtSettings.Key);

if (keyBytes.Length < 32)
{
    throw new InvalidOperationException(
        "JWT key must be at least 256 bits.");
}

var entraSettings = builder.Configuration
    .GetSection("Entra")
    .Get<EntraSettings>()
    ?? throw new InvalidOperationException("Entra configuration is missing.");

if (string.IsNullOrWhiteSpace(entraSettings.TenantId) ||
    string.IsNullOrWhiteSpace(entraSettings.ClientId) ||
    string.IsNullOrWhiteSpace(entraSettings.Audience))
{
    throw new InvalidOperationException(
        "Entra configuration is incomplete. Set Entra:TenantId, Entra:ClientId " +
        "and Entra:Audience (see appsettings.Development.json).");
}

var entraAuthority = $"https://login.microsoftonline.com/{entraSettings.TenantId}/v2.0";

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
            ValidIssuer = jwtSettings.Issuer,

            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),

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
            ValidAudience = entraSettings.Audience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddPolicyScheme("Smart", "Smart", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // Peek at the (still unvalidated) issuer claim only to route the
            // token to the handler that will actually validate it. This is
            // not authentication -- the chosen JwtBearer handler still
            // performs full signature/issuer/audience/lifetime validation.
            var authorizationHeader = context.Request.Headers.Authorization.ToString();

            if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authorizationHeader["Bearer ".Length..].Trim();
                var handler = new JwtSecurityTokenHandler();

                try
                {
                    if (handler.CanReadToken(token) &&
                        handler.ReadJwtToken(token).Issuer
                            .StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Entra";
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed token (e.g. not valid Base64Url). Fall through so the
                    // InternalJwt handler rejects it properly with a 401.
                }
            }

            return "InternalJwt";
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("can-edit-quotes", policy =>
        policy.RequireClaim("scope", "quotes.write"));
});

builder.Services.AddSingleton<IAuthorizationHandler, CanModifyOwnQuoteHandler>();
builder.Services.AddSingleton(jwtSettings);

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=quotes.db";

builder.Services.AddDbContext<QuotesDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();

var app = builder.Build();

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
        JwtSettings jwtSettings,
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
            CreateAccessToken(user, jwtSettings);

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
                jwtSettings.AccessTokenMinutes * 60
        });
    });

    // ---------------------------------------------------------
    // REFRESH TOKEN ROTATION
    // ---------------------------------------------------------

    authGroup.MapPost("/refresh", async (
        RefreshTokenRequest request,
        QuotesDbContext db,
        JwtSettings jwtSettings,
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

        // -----------------------------------------------------
        // REUSE DETECTION
        // -----------------------------------------------------

        if (storedToken.RevokedAt is not null)
        {
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId} and family {FamilyId}",
                storedToken.UserId,
                storedToken.FamilyId);

            var familyTokens = await db.RefreshTokens
                .Where(t =>
                    t.FamilyId == storedToken.FamilyId)
                .ToListAsync(cancellationToken);

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

        if (storedToken.ExpiresAt <=
            DateTimeOffset.UtcNow)
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
                jwtSettings);

        await db.SaveChangesAsync(
            cancellationToken);

        return Results.Ok(new
        {
            access_token = accessToken,
            refresh_token = newRefreshToken,
            expires_in =
                jwtSettings.AccessTokenMinutes * 60
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
    JwtSettings jwtSettings)
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
            jwtSettings.Issuer),

        new Claim(
            JwtRegisteredClaimNames.Aud,
            jwtSettings.Audience)
    };

    var token = new JwtSecurityToken(
        issuer: jwtSettings.Issuer,
        audience: jwtSettings.Audience,
        claims: claims,
        expires:
            DateTime.UtcNow.AddMinutes(
                jwtSettings.AccessTokenMinutes),
        signingCredentials:
            new SigningCredentials(
                new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(
                        jwtSettings.Key)),
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