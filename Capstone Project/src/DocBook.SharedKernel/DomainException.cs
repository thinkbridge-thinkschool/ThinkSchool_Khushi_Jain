namespace DocBook.SharedKernel;

// Thrown when a caller asks an aggregate to break one of its rules.
public sealed class DomainException(string message) : Exception(message);
