using FluentAssertions;
using QuotesApi.Models;

namespace QuotesApi.Tests;

public class QuoteTests
{
    [Fact]
    public void Create_WithValidAuthorAndText_ReturnsQuoteWithTrimmedValues()
    {
        var author = "  Marcus Aurelius  ";
        var text = "  You have power over your mind, not outside events.  ";

        var quote = Quote.Create(author, text, "owner-1");

        quote.Author.Should().Be("Marcus Aurelius");
        quote.Text.Should().Be("You have power over your mind, not outside events.");
        quote.OwnerId.Should().Be("owner-1");
        quote.IsDeleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithMissingAuthor_ThrowsQuoteDomainException(string? author)
    {
        Action act = () => Quote.Create(author, "Valid text", "owner-1");

        act.Should().Throw<QuoteDomainException>()
            .WithMessage("Author is required.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithMissingText_ThrowsQuoteDomainException(string? text)
    {
        Action act = () => Quote.Create("Valid author", text, "owner-1");

        act.Should().Throw<QuoteDomainException>()
            .WithMessage("Text is required.");
    }

    [Fact]
    public void Create_WithAuthorLongerThan200Characters_ThrowsQuoteDomainException()
    {
        var tooLongAuthor = new string('a', 201);

        Action act = () => Quote.Create(tooLongAuthor, "Valid text", "owner-1");

        act.Should().Throw<QuoteDomainException>()
            .WithMessage("Author must be 1–200 characters.");
    }

    [Fact]
    public void Create_WithAuthorExactly200Characters_Succeeds()
    {
        var maxLengthAuthor = new string('a', 200);

        var quote = Quote.Create(maxLengthAuthor, "Valid text", "owner-1");

        quote.Author.Should().HaveLength(200);
    }

    [Fact]
    public void Create_WithTextLongerThan1000Characters_ThrowsQuoteDomainException()
    {
        var tooLongText = new string('b', 1001);

        Action act = () => Quote.Create("Valid author", tooLongText, "owner-1");

        act.Should().Throw<QuoteDomainException>()
            .WithMessage("Text must be 1–1000 characters.");
    }

    [Fact]
    public void Create_WithTextExactly1000Characters_Succeeds()
    {
        var maxLengthText = new string('b', 1000);

        var quote = Quote.Create("Valid author", maxLengthText, "owner-1");

        quote.Text.Should().HaveLength(1000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithMissingOwnerId_ThrowsArgumentException(string? ownerId)
    {
        Action act = () => Quote.Create("Valid author", "Valid text", ownerId!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ValidationOrder_AuthorIsCheckedBeforeText()
    {
        // Both author and text are invalid; the Author failure should surface first,
        // matching the order of checks inside Quote.Create.
        Action act = () => Quote.Create(null, null, "owner-1");

        act.Should().Throw<QuoteDomainException>()
            .WithMessage("Author is required.");
    }

    [Fact]
    public void SoftDelete_OnActiveQuote_SetsIsDeletedToTrue()
    {
        var quote = Quote.Create("Author", "Text", "owner-1");

        quote.SoftDelete();

        quote.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void SoftDelete_CalledTwice_RemainsDeletedWithoutError()
    {
        var quote = Quote.Create("Author", "Text", "owner-1");

        quote.SoftDelete();
        quote.SoftDelete();

        quote.IsDeleted.Should().BeTrue();
    }
}
