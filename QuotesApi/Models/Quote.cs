namespace QuotesApi.Models;

public sealed class Quote
{
    private Quote()
    {
    }

    private Quote(string author, string text, string ownerId)
    {
        Author = author;
        Text = text;
        OwnerId = ownerId;
    }

    public int Id { get; private set; }

    public string Author { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public bool IsDeleted { get; private set; }

    /// <summary>
    /// The "sub" claim of the identity that created this quote. Null for quotes
    /// created before ownership tracking was introduced.
    /// </summary>
    public string? OwnerId { get; private set; }

    public static Quote Create(string? author, string? text, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new QuoteDomainException("Author is required.");

        author = author.Trim();
        if (author.Length is < 1 or > 200)
            throw new QuoteDomainException("Author must be 1–200 characters.");

        if (string.IsNullOrWhiteSpace(text))
            throw new QuoteDomainException("Text is required.");

        text = text.Trim();
        if (text.Length is < 1 or > 1000)
            throw new QuoteDomainException("Text must be 1–1000 characters.");

        if (string.IsNullOrWhiteSpace(ownerId))
            throw new QuoteDomainException("Quote owner is required.");

        return new Quote(author, text, ownerId);
    }

    public void SoftDelete() => IsDeleted = true;
}