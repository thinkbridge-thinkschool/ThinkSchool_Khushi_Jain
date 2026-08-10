namespace QuotesApi.Models;

public sealed class CollectionInvariantException : Exception
{
    public CollectionInvariantException(string message)
        : base(message)
    {
    }
}