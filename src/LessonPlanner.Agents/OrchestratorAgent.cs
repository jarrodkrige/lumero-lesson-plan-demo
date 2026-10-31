using LessonPlanner.Core;

namespace LessonPlanner.Agents;

public sealed class OrchestratorAgent(IRoleAgentProvider provider, ICurriculumRetriever retriever, AgentExecutionLimits limits)
    : IOrchestratorAgent
{
    public async Task<OrchestrationPlan> PlanAsync(LessonRequest request, CancellationToken cancellationToken)
    {
        DomainRules.Require(DomainRules.ValidateRequest(request));
        var catalogue = retriever.Sources.ToArray();
        AgentContractGuard.RequireCatalogue(catalogue);
        var plan = await new AgentModelRunner(provider, limits).RunAsync<OrchestrationPlan>(AgentRole.Orchestrator,
            AgentInstructions.Orchestrator, new
            {
                request,
                catalogue = catalogue.Select(source => new { source.Id, source.Title }).ToArray()
            }, 6000, cancellationToken).ConfigureAwait(false);
        AgentContractGuard.RequirePlan(plan, catalogue);
        return plan;
    }
}
