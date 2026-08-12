using Microsoft.AspNetCore.Authorization;
using QuotesApi.Models;

namespace QuotesApi.Authorization;

/// <summary>
/// Resource-based handler: the caller supplies the already-loaded Quote as the
/// authorization resource, so this handler never queries the database itself.
/// </summary>
public sealed class CanModifyOwnQuoteHandler(ILogger<CanModifyOwnQuoteHandler> logger)
    : AuthorizationHandler<CanModifyOwnQuoteRequirement, Quote>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CanModifyOwnQuoteRequirement requirement,
        Quote resource)
    {
        var subjectId = context.User.GetSubjectId();

        if (!string.IsNullOrEmpty(subjectId) &&
            string.Equals(resource.OwnerId, subjectId, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        else
        {
            logger.LogWarning(
                "User {SubjectId} was denied modification of quote {QuoteId} owned by {OwnerId}",
                subjectId,
                resource.Id,
                resource.OwnerId);
        }

        return Task.CompletedTask;
    }
}
