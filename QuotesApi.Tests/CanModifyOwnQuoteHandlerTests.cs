using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using QuotesApi.Authorization;
using QuotesApi.Models;

namespace QuotesApi.Tests;

public class CanModifyOwnQuoteHandlerTests
{
    [Fact]
    public async Task HandleAsync_UserSubjectMatchesQuoteOwner_Succeeds()
    {
        var logger = Substitute.For<ILogger<CanModifyOwnQuoteHandler>>();
        var handler = new CanModifyOwnQuoteHandler(logger);
        var quote = Quote.Create("Author", "Text", "owner-1");
        var user = PrincipalWithSubject("owner-1");
        var context = new AuthorizationHandlerContext(
            [new CanModifyOwnQuoteRequirement()], user, quote);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_UserSubjectDiffersFromQuoteOwner_DoesNotSucceed()
    {
        var logger = Substitute.For<ILogger<CanModifyOwnQuoteHandler>>();
        var handler = new CanModifyOwnQuoteHandler(logger);
        var quote = Quote.Create("Author", "Text", "owner-1");
        var user = PrincipalWithSubject("someone-else");
        var context = new AuthorizationHandlerContext(
            [new CanModifyOwnQuoteRequirement()], user, quote);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_QuoteHasNoOwner_DoesNotSucceed()
    {
        // Simulates a quote created before ownership tracking existed (OwnerId
        // is null). Quote.Create always requires an owner today, so this legacy
        // state is reproduced via reflection rather than the public factory.
        var logger = Substitute.For<ILogger<CanModifyOwnQuoteHandler>>();
        var handler = new CanModifyOwnQuoteHandler(logger);
        var quote = Quote.Create("Author", "Text", "owner-1");
        typeof(Quote).GetProperty(nameof(Quote.OwnerId))!.SetValue(quote, null);
        var user = PrincipalWithSubject("owner-1");
        var context = new AuthorizationHandlerContext(
            [new CanModifyOwnQuoteRequirement()], user, quote);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_UserHasNoSubjectClaim_DoesNotSucceed()
    {
        var logger = Substitute.For<ILogger<CanModifyOwnQuoteHandler>>();
        var handler = new CanModifyOwnQuoteHandler(logger);
        var quote = Quote.Create("Author", "Text", "owner-1");
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = new AuthorizationHandlerContext(
            [new CanModifyOwnQuoteRequirement()], user, quote);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static ClaimsPrincipal PrincipalWithSubject(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)]));
}
