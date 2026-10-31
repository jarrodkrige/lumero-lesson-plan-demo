using System.ComponentModel;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using ModelContextProtocol.Server;

namespace LessonPlanner.Mcp;

[McpServerToolType]
public sealed class QuizPublicationTool(IQuizStore store, IHttpContextAccessor contexts)
{
    [McpServerTool(Name = ContractVersions.PublishAction, UseStructuredContent = true, ReadOnly = false,
        Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Publishes a reviewed immutable five-question quiz using an existing presenter approval. Returns its durable publication receipt. It cannot create or grant approval.")]
    public Task<PublishedQuiz> PublishAsync(ApprovedPublication request, CancellationToken cancellationToken)
    {
        var context = contexts.HttpContext ?? throw new DomainException("forbidden", "An authenticated HTTP context is required.");
        var principal = context.User;
        var tenant = principal.FindFirst("tid")?.Value;
        var clientId = principal.FindFirst("azp")?.Value;
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(tenant, out var tenantId) || !Guid.TryParse(clientId, out var applicationId) ||
            !Guid.TryParse(context.Request.Headers[OfficialMcpPublicationClient.PresenterTenantHeader].ToString(), out var presenterTenant) ||
            !Guid.TryParse(context.Request.Headers[OfficialMcpPublicationClient.PresenterObjectHeader].ToString(), out var presenterObject) ||
            presenterTenant != tenantId || presenterObject == Guid.Empty)
            throw new DomainException("forbidden", "A trusted presenter assertion is required.");
        return store.PublishAsync(request, new(new(presenterTenant, presenterObject), applicationId.ToString("D")), cancellationToken);
    }
}
