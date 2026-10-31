using System.Security.Claims;
using LessonPlanner.Core;

namespace LessonPlanner.Web.Security;

public sealed record PresenterPolicy(Guid TenantId, IReadOnlySet<Guid> AllowedObjectIds, string CallerClientId)
{
    public bool Allows(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true &&
        Guid.TryParse(principal.FindFirst("tid")?.Value, out var tenant) && tenant == TenantId &&
        Guid.TryParse(principal.FindFirst("oid")?.Value, out var subject) && AllowedObjectIds.Contains(subject);
}

public sealed class PresenterAccess(IHttpContextAccessor contexts, PresenterPolicy policy)
{
    public PresenterIdentity RequireIdentity()
    {
        var user = contexts.HttpContext?.User;
        if (user is null || !policy.Allows(user)) throw new DomainException("forbidden", "Presenter access is required.");
        return new(policy.TenantId, Guid.Parse(user.FindFirst("oid")!.Value));
    }

    public PublicationCaller RequireCaller() => new(RequireIdentity(), policy.CallerClientId);
}
