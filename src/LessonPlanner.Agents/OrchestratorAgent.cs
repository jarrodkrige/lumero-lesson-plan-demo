using System.Collections.Immutable;
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
        var sortingTrace = SortingWorkedExamples.Applies(request);
        if (sortingTrace && SortingTeachingSources.Required.Any(expected => !catalogue.Contains(expected)))
            throw new DomainException("curriculum_catalogue", "The required sorting teaching sources are unavailable or invalid.");
        var available = sortingTrace ? catalogue : catalogue.Where(source =>
            !SortingTeachingSources.All.Any(oak => oak.Id == source.Id)).ToArray();
        AgentContractGuard.RequireCatalogue(available);
        var plan = await new AgentModelRunner(provider, limits).RunAsync<OrchestrationPlan>(AgentRole.Orchestrator,
            AgentInstructions.Orchestrator, new
            {
                request,
                catalogue = available.Select(source => new { source.Id, source.Title }).ToArray()
            }, 6000, cancellationToken).ConfigureAwait(false);
        AgentContractGuard.RequirePlan(plan, available);
        return sortingTrace ? plan with
        {
            SourceIds = SortingTeachingSources.Required.Select(source => source.Id).ToImmutableArray()
        } : plan;
    }
}
