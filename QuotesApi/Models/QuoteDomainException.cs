namespace QuotesApi.Models;

public sealed class QuoteDomainException(string message)
    : Exception(message);
