namespace QuotesApi.Models;

public sealed class CollectionDomainException(string message)
    : Exception(message);
