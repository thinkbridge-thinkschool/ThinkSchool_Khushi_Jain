namespace QuotesApi.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}