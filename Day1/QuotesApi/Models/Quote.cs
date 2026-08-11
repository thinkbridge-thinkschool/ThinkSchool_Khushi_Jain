namespace QuotesApi.Models;

public sealed class Quote
{
    private Quote()
    {
    }

    private Quote(string author, string text)
    {
        Author = author;
        Text = text;
    }

    public int Id { get; private set; }

    public string Author { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public bool IsDeleted { get; private set; }

    public static Quote Create(string? author, string? text)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new QuoteDomainException("Author is required.");

        author = author.Trim();

        if (author.Length is < 1 or > 200)
            throw new QuoteDomainException(
                "Author must be 1–200 characters.");

        if (string.IsNullOrWhiteSpace(text))
            throw new QuoteDomainException("Text is required.");

        text = text.Trim();

        if (text.Length is < 1 or > 1000)
            throw new QuoteDomainException(
                "Text must be 1–1000 characters.");

        return new Quote(author, text);
    }

    public void SoftDelete() => IsDeleted = true;
}