using Microsoft.AspNetCore.Hosting;

namespace QuotesApi.Tests;

/// <summary>
/// Configuration the application expects from user secrets, which CI does not
/// have. Every test host applies these synthetic values instead; none of them
/// carries meaning outside the test process.
/// </summary>
internal static class TestConfiguration
{
    public const string JwtSigningKey = "test-only-signing-key-not-a-secret!!";

    public const string SeedAdminEmail = "seed-admin@quotes.test";

    public const string SeedAdminPassword = "test-only-seed-password!1";

    public static IWebHostBuilder UseTestSecrets(this IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("Seed:AdminEmail", SeedAdminEmail);
        builder.UseSetting("Seed:AdminPassword", SeedAdminPassword);

        return builder;
    }
}
