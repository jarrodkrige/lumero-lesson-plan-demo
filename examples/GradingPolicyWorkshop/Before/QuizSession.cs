using LessonPlanner.Core;

namespace GradingPolicyWorkshop.Before;

public sealed class QuizSession
{
    public QuizResult Submit(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
    {
        var result = new ExactMatchGradingPolicy().Grade(quiz, attempt, submittedAt);
        var feedback = result.Score switch
        {
            5 => "Explain why the sorted finish times still contain every robot.",
            >= 3 => "Trace another pass and explain each swap to a partner.",
            _ => "Build a trace table with a partner and compare neighbouring times."
        };
        return result with { Feedback = feedback };
    }
}
