using FluentAssertions;
using QuotesApi.Models;

namespace QuotesApi.Tests;

public class QuoteDomainTests
{
    [Fact]
    public void Create_WithValidData_CreatesQuote()
    {
        var quote = Quote.Create("Albert Einstein", "Life is beautiful.");

        quote.Author.Should().Be("Albert Einstein");
        quote.Text.Should().Be("Life is beautiful.");
        quote.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Create_WithEmptyAuthor_Throws()
    {
        Action act = () => Quote.Create("", "Some text");

        act.Should().Throw<QuoteDomainException>();
    }

    [Fact]
    public void Create_WithAuthorOver200Characters_Throws()
    {
        var author = new string('A', 201);

        Action act = () => Quote.Create(author, "Some text");

        act.Should().Throw<QuoteDomainException>();
    }

    [Fact]
    public void Create_WithEmptyText_Throws()
    {
        Action act = () => Quote.Create("Author", "");

        act.Should().Throw<QuoteDomainException>();
    }

    [Fact]
    public void Create_WithTextOver1000Characters_Throws()
    {
        var text = new string('A', 1001);

        Action act = () => Quote.Create("Author", text);

        act.Should().Throw<QuoteDomainException>();
    }

    [Fact]
    public void SoftDelete_SetsIsDeleted()
    {
        var quote = Quote.Create("Author", "Some text");

        quote.SoftDelete();

        quote.IsDeleted.Should().BeTrue();
    }
}
