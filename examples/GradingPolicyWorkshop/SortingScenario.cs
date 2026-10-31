using System.Collections.Immutable;
using LessonPlanner.Core;

namespace GradingPolicyWorkshop;

// Original synthetic data only. These answer keys and fixed identifiers are not live publication data.
public static class SortingScenario
{
    public static DateTimeOffset SubmittedAt { get; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public static QuizContent CreateQuiz() => new("Synthetic robot sports day sorting", [
        new("q1", "Robot finish times are [18, 12, 18, 15] seconds. What is their ascending order?",
            [new("a", "12, 15, 18, 18"), new("b", "18, 18, 15, 12"),
             new("c", "12, 15, 18"), new("d", "12, 18, 15, 18")],
            "a", "Ascending order puts smaller times first and keeps both 18-second results.", []),
        new("q2", "Which items does a left-to-right bubble-sort pass compare?",
            [new("a", "Only the first and last items"), new("b", "Neighbouring items"),
             new("c", "Only items already in order"), new("d", "Every item with itself")],
            "b", "A bubble-sort pass compares neighbouring items and swaps them if they are out of order.", []),
        new("q3", "Make one left-to-right ascending bubble-sort pass over [24, 11, 19, 15]. What is the list now?",
            [new("a", "11, 15, 19, 24"), new("b", "24, 19, 15, 11"),
             new("c", "11, 19, 15, 24"), new("d", "11, 24, 19, 15")],
            "c", "The pass moves 24 to the end. The middle pair 19 and 15 still needs another pass.", []),
        new("q4", "Insertion sort places 14 into the sorted section [9, 17, 23]. What is the new sorted section?",
            [new("a", "9, 14, 17, 23"), new("b", "14, 9, 17, 23"),
             new("c", "9, 17, 23, 14"), new("d", "9, 14, 23, 17")],
            "a", "Shift 23 and 17 one place to the right, then insert 14 after 9.", []),
        new("q5", "Merge the sorted lists [10, 16] and [12, 16] in ascending order. Which result keeps every item?",
            [new("a", "10, 12, 16"), new("b", "10, 12, 16, 16"),
             new("c", "16, 16, 12, 10"), new("d", "10, 16, 12, 16")],
            "b", "Choose the smaller remaining front item each time and retain both copies of 16.", [])
    ]);

    public static QuizAttempt CreateAttempt(int correctAnswers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(correctAnswers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(correctAnswers, ContractVersions.QuestionCount);

        var answers = CreateQuiz().Questions.Select((question, index) => new QuizAnswer(question.Id,
            index < correctAnswers
                ? question.CorrectOptionId
                : question.Options.First(option => option.Id != question.CorrectOptionId).Id)).ToImmutableArray();
        return new(new string('a', 48), new string('b', 48), answers);
    }
}
