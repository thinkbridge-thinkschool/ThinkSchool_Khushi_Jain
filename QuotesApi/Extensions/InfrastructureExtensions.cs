using System.Diagnostics;
using System.Text;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuotesApi.Authorization;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Resilience;
using QuotesApi.Services;
using QuotesApi.Time;
using Serilog;

namespace QuotesApi.Extensions;

public static class InfrastructureExtensions
{
    public static WebApplicationBuilder AddInfrastructure(this WebApplicationBuilder builder)
    {
        // Order matters: Key Vault has to join the configuration sources before
        // anything binds options out of them.
        builder.AddKeyVaultConfiguration();
        builder.AddStructuredLogging();
        builder.AddTelemetry();
        builder.AddApiOptions();
        builder.AddApiAuthentication();
        builder.AddPersistence();
        builder.AddMessaging();

        return builder;
    }

    /// <summary>
    /// The Key Vault URI is not a secret, so it is safe to check in. Everything
    /// it resolves never touches source control. DefaultAzureCredential uses
    /// the container's managed identity in Azure and falls back to the
    /// developer's Azure CLI login locally.
    /// </summary>
    private static void AddKeyVaultConfiguration(this WebApplicationBuilder builder)
    {
        var keyVaultUri = builder.Configuration["KeyVault:Uri"];

        if (!string.IsNullOrWhiteSpace(keyVaultUri))
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(keyVaultUri),
                new DefaultAzureCredential());
        }
    }

    private static void AddStructuredLogging(this WebApplicationBuilder builder) =>
        builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext());

    private static void AddTelemetry(this WebApplicationBuilder builder)
    {
        // Named source for the manual spans this app creates itself, separate
        // from the spans the automatic instrumentation creates. Registered as a
        // singleton so endpoint handlers can inject it.
        var activitySource = new ActivitySource("QuotesApi");
        builder.Services.AddSingleton(activitySource);

        var telemetryBuilder = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("QuotesApi"))
            .WithTracing(tracing => tracing
                .AddSource("QuotesApi")
                .AddAspNetCoreInstrumentation()

                // Emits a span per database command, which is what makes a
                // query-count problem such as an N+1 visible as repeated
                // sibling spans under one request rather than as a single slow
                // parent with no explanation inside it.
                .AddEntityFrameworkCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        // Only wire up the Azure Monitor exporter when a connection string is
        // actually configured, so local dev -- which only has the OTLP exporter
        // feeding Jaeger -- stays free of export warnings for a destination
        // that was never configured.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["AzureMonitor:ConnectionString"]) ||
            !string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            telemetryBuilder.UseAzureMonitor();
        }
    }

    private static void AddApiOptions(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks();

        // Minimal APIs do not evaluate DataAnnotations on their own the way an
        // [ApiController] does. This turns the attributes on the request
        // records into a filter that short-circuits with 400 and a
        // ValidationProblemDetails body before any handler runs.
        builder.Services.AddValidation();

        // ValidateOnStart forces these checks to run during startup rather than
        // on first use, so a missing key fails the same way a bad one does --
        // immediately, not on the first login attempt.
        builder.Services.AddOptions<JwtOptions>()
            .Bind(builder.Configuration.GetSection("Jwt"))
            .Validate(options => options.HasSigningKey, JwtOptions.MissingKeyMessage)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer),
                "Jwt:Issuer is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Audience),
                "Jwt:Audience is required.")
            .ValidateOnStart();

        builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection("Seed"));
    }

    private static void AddApiAuthentication(this WebApplicationBuilder builder)
    {
        // AddJwtBearer wires up authentication before builder.Build() runs --
        // too early to resolve IOptions<JwtOptions> from the container. This
        // one bootstrap read binds directly from configuration for that reason
        // only, and repeats the key check because ValidateOnStart has not run
        // yet; without it a missing key surfaces as an opaque "key length is
        // zero" failure from SymmetricSecurityKey.
        var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
            ?? throw new InvalidOperationException("JWT configuration is missing.");

        if (!jwtOptions.HasSigningKey)
        {
            throw new InvalidOperationException(JwtOptions.MissingKeyMessage);
        }

        var signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtOptions.SigningKey));

        var entraOptions = builder.Configuration.GetSection("Entra").Get<EntraOptions>()
            ?? throw new InvalidOperationException("Entra configuration is missing.");

        if (string.IsNullOrWhiteSpace(entraOptions.TenantId) ||
            string.IsNullOrWhiteSpace(entraOptions.ClientId) ||
            string.IsNullOrWhiteSpace(entraOptions.Audience))
        {
            throw new InvalidOperationException(
                "Entra configuration is incomplete. Set Entra:TenantId, Entra:ClientId " +
                "and Entra:Audience.");
        }

        var entraAuthority = $"https://login.microsoftonline.com/{entraOptions.TenantId}/v2.0";

        // The Entra scheme calls out to login.microsoftonline.com for OIDC
        // discovery and JWKS signing keys -- the one real external HTTP
        // dependency this API has -- so its backchannel gets retry, circuit
        // breaker and timeout.
        builder.Services.AddHttpClient(EntraHttpClientExtensions.ClientName)
            .AddEntraResilienceHandler();

        builder.Services.AddOptions<JwtBearerOptions>("Entra")
            .Configure<IHttpClientFactory>((options, factory) =>
            {
                options.Backchannel = factory.CreateClient(EntraHttpClientExtensions.ClientName);
            });

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
                    ValidIssuer = jwtOptions.Issuer,

                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            })
            .AddJwtBearer("Entra", options =>
            {
                // Authority drives OIDC discovery (signing keys), independent
                // of the explicit issuer/audience checks below. No client
                // secret is needed to validate bearer access tokens.
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
                // Peek at the still-unvalidated issuer claim only to route the
                // token to the handler that will actually validate it. The
                // chosen JwtBearer handler still performs full
                // signature/issuer/audience/lifetime validation.
                options.ForwardDefaultSelector = context =>
                    AuthenticationSchemeSelector.Select(
                        context.Request.Headers.Authorization.ToString());
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
    }

    private static void AddPersistence(this WebApplicationBuilder builder)
    {
        // Singleton: no state, no per-request dependencies, and safe to share
        // for the life of the app.
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<RefreshTokenEvaluator>();

        // Transient: stateless, but each call builds a JwtSecurityTokenHandler,
        // so a fresh instance per resolution avoids sharing that machinery
        // across concurrent requests.
        builder.Services.AddTransient<TokenService>();

        var connectionString =
            builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=quotes.db";

        builder.Services.AddDbContext<QuotesDbContext>(options =>
        {
            options.UseSqlite(connectionString);

            // Serilog already logs EF's commands at Debug in Development. This adds
            // the parameter values to them, which never belong in a deployed log.
            if (builder.Environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
            }
        });

        builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();
        builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();

        builder.Services.AddScoped<AddQuoteToCollectionHandler>();
        builder.Services.AddScoped<CollectionDetailsQuery>();
    }

    /// <summary>
    /// The outbox is always written; only the relay that drains it is optional.
    /// A row is durable either way, so switching the relay off delays delivery
    /// rather than losing it.
    /// </summary>
    private static void AddMessaging(this WebApplicationBuilder builder)
    {
        var outboxSection = builder.Configuration.GetSection("Outbox");

        builder.Services.Configure<OutboxOptions>(outboxSection);

        // Singleton: the write path and the relay have to share one instance
        // for a signal raised by the first to reach the second.
        builder.Services.AddSingleton<OutboxSignal>();

        // Singleton: stateless, and the broker-backed publisher that replaces it
        // will hold a connection that is meant to be shared.
        builder.Services.AddSingleton<IIntegrationEventPublisher, LoggingIntegrationEventPublisher>();

        var outboxOptions = outboxSection.Get<OutboxOptions>() ?? new OutboxOptions();

        if (outboxOptions.Enabled)
        {
            builder.Services.AddHostedService<OutboxRelay>();
        }
    }
}
