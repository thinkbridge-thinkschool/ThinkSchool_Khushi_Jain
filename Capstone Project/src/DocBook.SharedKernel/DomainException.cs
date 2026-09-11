namespace DocBook.SharedKernel;

// The code, not the message, is what the HTTP layer maps to a response.
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
