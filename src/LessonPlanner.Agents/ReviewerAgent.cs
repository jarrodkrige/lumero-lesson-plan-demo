using LessonPlanner.Core;

namespace LessonPlanner.Agents;

public sealed class ReviewerAgent(IRoleAgentProvider provider, AgentExecutionLimits limits, TimeProvider clock) : IReviewerAgent
{
    public Task<ReviewResult> ReviewLessonAsync(LessonRequest request, LessonPlan lesson, CancellationToken cancellationToken)
    {
        DomainRules.Require(DomainRules.ValidateRequest(request));
        AgentContractGuard.RequireSources(lesson.Sources, clock);
        DomainRules.Require(AgentContractGuard.Lesson(lesson, request));
        return ReviewAsync(new { task = "lesson", request, lesson = LessonBody.From(lesson),
            evidence = AgentContractGuard.Evidence(lesson.Sources) }, cancellationToken);
    }

    public Task<ReviewResult> ReviewQuizAsync(LessonPlan lesson, QuizContent quiz, CancellationToken cancellationToken)
    {
        AgentContractGuard.RequireSources(lesson.Sources, clock);
        DomainRules.Require(AgentContractGuard.Quiz(quiz, lesson.Sources));
        return ReviewAsync(new { task = "quiz", lesson = LessonBody.From(lesson), quiz,
            evidence = AgentContractGuard.Evidence(lesson.Sources) }, cancellationToken);
    }

    private async Task<ReviewResult> ReviewAsync(object input, CancellationToken cancellationToken)
    {
        var result = await new AgentModelRunner(provider, limits).RunAsync<ReviewResult>(AgentRole.Reviewer,
            AgentInstructions.Reviewer, input, 20_000, cancellationToken).ConfigureAwait(false);
        AgentContractGuard.RequireReview(result);
        return result;
    }
}
