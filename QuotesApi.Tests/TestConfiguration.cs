namespace QuotesApi.Tests;

internal static class TestConfiguration
{
    /// <summary>
    /// Signing key for the test hosts. The application reads Jwt:SigningKey
    /// from user-secrets, which CI does not have, so every
    /// WebApplicationFactory in this project supplies this synthetic value
    /// instead. It signs nothing outside the test process.
    /// </summary>
    public const string JwtSigningKey = "test-only-signing-key-not-a-secret!!";
}
