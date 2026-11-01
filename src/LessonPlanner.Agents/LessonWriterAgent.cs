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
        var context = new
        {
            request,
            curriculum = new { curriculum.Summary, curriculum.Requirements, evidence = AgentContractGuard.Evidence(curriculum.Sources) },
            previous = previous is null ? null : LessonBody.From(previous),
            feedback
        };
        var examples = SortingWorkedExamples.For(request, curriculum.Sources);
        object input = examples is null ? context : new
        {
            context.request, context.curriculum, applicationCalculatedExamples = examples, context.previous, context.feedback
        };
        var body = await new AgentModelRunner(provider, limits).RunAsync<LessonBody>(AgentRole.LessonWriter,
            AgentInstructions.Writer, input, 28_000, cancellationToken).ConfigureAwait(false);
        return body.WithSources(curriculum.Sources);
    }
}
