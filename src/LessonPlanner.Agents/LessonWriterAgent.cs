using LessonPlanner.Core;

namespace LessonPlanner.Agents;

public sealed class LessonWriterAgent(IRoleAgentProvider provider, AgentExecutionLimits limits, TimeProvider clock)
    : ILessonWriterAgent
{
    public async Task<LessonPlan> WriteAsync(LessonRequest request, CurriculumBrief curriculum, LessonPlan? previous,
        ReviewResult? feedback, CancellationToken cancellationToken)
    {
        DomainRules.Require(DomainRules.ValidateRequest(request));
        AgentContractGuard.RequireCurriculum(curriculum, clock);
        if (feedback is not null) AgentContractGuard.RequireReview(feedback);
        var body = await new AgentModelRunner(provider, limits).RunAsync<LessonBody>(AgentRole.LessonWriter,
            AgentInstructions.Writer, new
            {
                request,
                curriculum = new { curriculum.Summary, curriculum.Requirements, evidence = AgentContractGuard.Evidence(curriculum.Sources) },
                previous = previous is null ? null : LessonBody.From(previous),
                feedback
            }, 28_000, cancellationToken).ConfigureAwait(false);
        return body.WithSources(curriculum.Sources);
    }
}
