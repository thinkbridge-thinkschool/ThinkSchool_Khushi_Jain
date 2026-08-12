namespace QuotesApi.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
