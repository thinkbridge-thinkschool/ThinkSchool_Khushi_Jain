using System.Text;
using DocBook.Infrastructure;
using DocBook.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace DocBook.Api.Extensions;

public static class AuthenticationExtensions
{
    public static WebApplicationBuilder AddApiAuthentication(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration is missing.");

        // Checked here as well as in the options validator, which has not run this early.
        if (!options.HasSigningKey)
        {
            throw new InvalidOperationException(JwtOptions.MissingKeyMessage);
        }

        builder.Services.AddOptions<JwtOptions>()
            .Bind(builder.Configuration.GetSection("Jwt"))
            .Validate(bound => bound.HasSigningKey, JwtOptions.MissingKeyMessage)
            .ValidateOnStart();

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(bearer =>
            {
                // Off, so 'sub' and 'role' stay as written rather than becoming WS-Federation URIs.
                bearer.MapInboundClaims = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,

                    ValidateAudience = true,
                    ValidAudience = options.Audience,

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),

                    NameClaimType = ClaimNames.Subject,
                    RoleClaimType = ClaimNames.Role
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Staff, policy => policy.RequireRole(nameof(ActorRole.Staff)));

        return builder;
    }
}
