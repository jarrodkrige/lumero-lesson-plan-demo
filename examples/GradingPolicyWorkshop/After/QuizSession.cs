using LessonPlanner.Core;

namespace GradingPolicyWorkshop.After;

public sealed class QuizSession
{
    private readonly IGradingPolicy gradingPolicy;

    public QuizSession(IGradingPolicy gradingPolicy)
    {
        ArgumentNullException.ThrowIfNull(gradingPolicy);
        this.gradingPolicy = gradingPolicy;
    }

    public QuizResult Submit(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
    {
        return gradingPolicy.Grade(quiz, attempt, submittedAt);
    }
}
