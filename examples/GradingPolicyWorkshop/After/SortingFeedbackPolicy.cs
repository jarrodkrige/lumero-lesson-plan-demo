using LessonPlanner.Core;

namespace GradingPolicyWorkshop.After;

public sealed class SortingFeedbackPolicy : IGradingPolicy
{
    private readonly IGradingPolicy markingPolicy;

    public SortingFeedbackPolicy(IGradingPolicy markingPolicy)
    {
        ArgumentNullException.ThrowIfNull(markingPolicy);
        this.markingPolicy = markingPolicy;
    }

    public QuizResult Grade(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
    {
        var result = markingPolicy.Grade(quiz, attempt, submittedAt);
        var feedback = result.Score switch
        {
            5 => "Explain why the sorted finish times still contain every robot.",
            >= 3 => "Trace another pass and explain each swap to a partner.",
            _ => "Build a trace table with a partner and compare neighbouring times."
        };
        return result with { Feedback = feedback };
    }
}
