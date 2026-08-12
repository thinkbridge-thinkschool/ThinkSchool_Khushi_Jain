using System.Text.Json.Serialization;

namespace QuotesApi.Contracts;

public record RefreshTokenRequest(
    [property: JsonPropertyName("refresh_token")]
    string RefreshToken);