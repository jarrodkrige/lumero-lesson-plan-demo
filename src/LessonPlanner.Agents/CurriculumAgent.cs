using System.Collections.Immutable;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

public sealed class CurriculumAgent(IRoleAgentProvider provider, ICurriculumRetriever retriever,
    AgentExecutionLimits limits, TimeProvider clock) : ICurriculumAgent
{
    public async Task<CurriculumBrief> AnalyseAsync(LessonRequest request, OrchestrationPlan plan,
        CancellationToken cancellationToken)
    {
        DomainRules.Require(DomainRules.ValidateRequest(request));
        limits.Validate();
        var catalogue = retriever.Sources.ToArray();
        AgentContractGuard.RequirePlan(plan, catalogue);
        var retrieved = ImmutableArray.CreateBuilder<RetrievedSource>();
        foreach (var id in plan.SourceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await AgentOperations.BoundedAsync(token => retriever.RetrieveAsync(id, token),
                limits.RetrievalTimeout, "retrieval_timeout", cancellationToken).ConfigureAwait(false);
            if (source is null || source.Reference is null || string.IsNullOrWhiteSpace(source.Content) ||
                source.Content.Length > limits.MaximumSourceCharacters)
                throw new DomainException("curriculum_retrieval", "The required curriculum content is unavailable or exceeds the retrieval budget.");
            var entry = catalogue.Single(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            AgentContractGuard.RequireSources([source.Reference], clock);
            AgentContractGuard.RequireSourceBinding(source.Reference, entry);
            retrieved.Add(source);
        }

        var analysis = await new AgentModelRunner(provider, limits).RunAsync<CurriculumAnalysis>(AgentRole.Curriculum,
            AgentInstructions.Curriculum, new
            {
                request,
                plan,
                sources = retrieved.Select(source => new { source.Reference.Id, source.Reference.Title, source.Content }).ToArray()
            }, 16_000, cancellationToken).ConfigureAwait(false);
        var brief = new CurriculumBrief(analysis.Summary, analysis.Requirements,
            retrieved.Select(source => source.Reference).ToImmutableArray());
        AgentContractGuard.RequireCurriculum(brief, clock, plan);
        return brief;
    }
}
