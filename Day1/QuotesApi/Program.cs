using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
    throw new InvalidOperationException("JWT key must be at least 256 bits.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
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
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton(jwtSettings);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
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
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
    await db.Database.MigrateAsync();

    if (!await db.Users.AnyAsync())
    {
        db.Users.Add(new User
        {
            Email = "admin@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("P@ssword1")
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
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["page/size"] = ["Page must be >= 1 and size must be between 1 and 100."]
            });
        }

        var result = await repository.GetPagedAsync(
            currentPage, pageSize, cancellationToken);

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
        var quote = await repository.GetByIdAsync(id, cancellationToken);

        return quote is null
            ? Results.NotFound()
            : Results.Ok(quote);
    });

    group.MapPost("/", async (
        CreateQuoteRequest request,
        IQuoteRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        Quote quote;

        try
        {
            quote = Quote.Create(request.Author, request.Text);
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

        logger.LogInformation("Created quote {QuoteId}", quote.Id);

        return Results.Created($"/api/quotes/{quote.Id}", quote);
    }).RequireAuthorization();

    group.MapDelete("/{id:int}", async (
        int id,
        IQuoteRepository repository,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        var deleted = await repository.DeleteAsync(id, cancellationToken);

        if (!deleted)
            return Results.NotFound();

        logger.LogInformation("Deleted quote {QuoteId}", id);

        return Results.NoContent();    }).RequireAuthorization();
}

static void MapAuthEndpoints(WebApplication app)
{
    var authGroup = app.MapGroup("/api/auth");

    authGroup.MapPost("/login", async (
        LoginRequest request,
        QuotesDbContext db,
        JwtSettings jwtSettings,
        CancellationToken cancellationToken) =>
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == request.Email, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Results.Unauthorized();

        var expiresIn = jwtSettings.AccessTokenMinutes * 60;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iss, jwtSettings.Issuer),
            new Claim(JwtRegisteredClaimNames.Aud, jwtSettings.Audience)
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings.Issuer,
            audience: jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(jwtSettings.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
                SecurityAlgorithms.HmacSha256));

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        return Results.Ok(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = expiresIn
        });
    });
}

