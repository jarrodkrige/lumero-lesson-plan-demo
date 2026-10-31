using System.Collections.Immutable;

namespace LessonPlanner.Core;

public sealed class ExactMatchGradingPolicy : IGradingPolicy
{
    public QuizResult Grade(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
    {
        DomainRules.RequireId(attempt.Id);
        DomainRules.RequireId(attempt.QuizId);
        if (attempt.Answers.IsDefault || attempt.Answers.Length != ContractVersions.QuestionCount ||
            attempt.Answers.Any(a => a is null || string.IsNullOrEmpty(a.QuestionId) || string.IsNullOrEmpty(a.OptionId)) ||
            attempt.Answers.Select(a => a.QuestionId).Distinct(StringComparer.Ordinal).Count() != ContractVersions.QuestionCount)
            throw new DomainException("invalid_answers", "Answer each question once before submitting.");

        var answers = attempt.Answers.ToDictionary(a => a.QuestionId, StringComparer.Ordinal);
        var marked = ImmutableArray.CreateBuilder<MarkedQuestion>(ContractVersions.QuestionCount);
        foreach (var question in quiz.Questions)
        {
            if (!answers.TryGetValue(question.Id, out var answer) ||
                !question.Options.Any(o => o.Id == answer.OptionId))
                throw new DomainException("invalid_answers", "Select one of the provided options for each question.");
            marked.Add(new(question.Id, question.Prompt, answer.OptionId, question.CorrectOptionId,
                answer.OptionId == question.CorrectOptionId, question.Explanation));
        }
        var score = marked.Count(q => q.IsCorrect);
        var feedback = score switch
        {
            5 => "All five answers are correct. Explain one of the algorithms to a partner.",
            >= 3 => "Review the explanations for the questions you missed, then trace another example.",
            _ => "Revisit the worked examples with your teacher and use the explanations below."
        };
        return new(attempt.Id, attempt.QuizId, score, ContractVersions.QuestionCount, feedback,
            marked.ToImmutable(), submittedAt);
    }
}
