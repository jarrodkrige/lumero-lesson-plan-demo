using System.Text.Json;
using GradingPolicyWorkshop;
using GradingPolicyWorkshop.After;
using LessonPlanner.Core;
using BeforeSession = GradingPolicyWorkshop.Before.QuizSession;
using AfterSession = GradingPolicyWorkshop.After.QuizSession;

namespace LessonPlanner.UnitTests;

public sealed class GradingPolicyWorkshopExampleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void BeforeAndAfterKeepProductionMarking(int correctAnswers)
    {
        var quiz = SortingScenario.CreateQuiz();
        var attempt = SortingScenario.CreateAttempt(correctAnswers);
        var submittedAt = SortingScenario.SubmittedAt;
        var expected = new ExactMatchGradingPolicy().Grade(quiz, attempt, submittedAt);

        var before = new BeforeSession().Submit(quiz, attempt, submittedAt);
        var after = NewSession().Submit(quiz, attempt, submittedAt);

        Assert.Equal(correctAnswers, expected.Score);
        Assert.Equal(5, expected.Total);
        AssertSameMarking(expected, before);
        AssertSameMarking(expected, after);
    }

    [Theory]
    [InlineData(0, "Build a trace table with a partner and compare neighbouring times.")]
    [InlineData(1, "Build a trace table with a partner and compare neighbouring times.")]
    [InlineData(2, "Build a trace table with a partner and compare neighbouring times.")]
    [InlineData(3, "Trace another pass and explain each swap to a partner.")]
    [InlineData(4, "Trace another pass and explain each swap to a partner.")]
    [InlineData(5, "Explain why the sorted finish times still contain every robot.")]
    public void FeedbackBandsAreExplicit(int correctAnswers, string expectedFeedback)
    {
        var result = NewSession().Submit(SortingScenario.CreateQuiz(),
            SortingScenario.CreateAttempt(correctAnswers), SortingScenario.SubmittedAt);

        Assert.Equal(expectedFeedback, result.Feedback);
    }

    [Fact]
    public void SessionUsesTheInjectedPolicyOnceAndReturnsItsResult()
    {
        var quiz = SortingScenario.CreateQuiz();
        var attempt = SortingScenario.CreateAttempt(3);
        var submittedAt = SortingScenario.SubmittedAt;
        var expected = new ExactMatchGradingPolicy().Grade(quiz, attempt, submittedAt);
        var policy = new RecordingPolicy(expected);

        var actual = new AfterSession(policy).Submit(quiz, attempt, submittedAt);

        Assert.Same(expected, actual);
        Assert.Equal(1, policy.Calls);
        Assert.Same(quiz, policy.SeenQuiz);
        Assert.Same(attempt, policy.SeenAttempt);
        Assert.Equal(submittedAt, policy.SeenSubmittedAt);
    }

    [Fact]
    public void FeedbackPolicyChangesOnlyTheMessageOnTheInjectedMarkingResult()
    {
        var quiz = SortingScenario.CreateQuiz();
        var attempt = SortingScenario.CreateAttempt(4);
        var submittedAt = SortingScenario.SubmittedAt;
        var original = new ExactMatchGradingPolicy().Grade(quiz, attempt, submittedAt)
            with { Feedback = "Original feedback for the recording test." };
        var marking = new RecordingPolicy(original);

        var actual = new SortingFeedbackPolicy(marking).Grade(quiz, attempt, submittedAt);

        Assert.Equal(1, marking.Calls);
        Assert.Same(quiz, marking.SeenQuiz);
        Assert.Same(attempt, marking.SeenAttempt);
        Assert.Equal(submittedAt, marking.SeenSubmittedAt);
        Assert.NotEqual(original.Feedback, actual.Feedback);
        Assert.Equal(original with { Feedback = actual.Feedback }, actual);
        Assert.Equal("Original feedback for the recording test.", original.Feedback);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown-question")]
    [InlineData("unknown-option")]
    [InlineData("extra")]
    [InlineData("default")]
    public void InvalidAnswersStillFailInsteadOfReceivingFeedback(string answerSet)
    {
        var quiz = SortingScenario.CreateQuiz();
        var attempt = SortingScenario.CreateAttempt(3);
        var invalid = attempt with
        {
            Answers = answerSet switch
            {
                "missing" => attempt.Answers.RemoveAt(4),
                "duplicate" => attempt.Answers.SetItem(1, attempt.Answers[0]),
                "unknown-question" => attempt.Answers.SetItem(0, new("q99", "a")),
                "unknown-option" => attempt.Answers.SetItem(0, new("q1", "not-an-option")),
                "extra" => attempt.Answers.Add(new("q6", "a")),
                "default" => default,
                _ => throw new ArgumentException("Unknown test case.", nameof(answerSet))
            }
        };

        var beforeError = Assert.Throws<DomainException>(() =>
            new BeforeSession().Submit(quiz, invalid, SortingScenario.SubmittedAt));
        var afterError = Assert.Throws<DomainException>(() =>
            NewSession().Submit(quiz, invalid, SortingScenario.SubmittedAt));

        Assert.Equal("invalid_answers", beforeError.Code);
        Assert.Equal(beforeError.Code, afterError.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAttemptOrQuizIdentifiersAreStillRejected(bool invalidQuizId)
    {
        var quiz = SortingScenario.CreateQuiz();
        var attempt = SortingScenario.CreateAttempt(3);
        var invalid = invalidQuizId ? attempt with { QuizId = "not-a-share-token" }
            : attempt with { Id = "not-an-attempt-token" };

        var beforeError = Assert.Throws<DomainException>(() =>
            new BeforeSession().Submit(quiz, invalid, SortingScenario.SubmittedAt));
        var afterError = Assert.Throws<DomainException>(() =>
            NewSession().Submit(quiz, invalid, SortingScenario.SubmittedAt));

        Assert.Equal("not_found", beforeError.Code);
        Assert.Equal(beforeError.Code, afterError.Code);
    }

    [Fact]
    public void SyntheticFixtureKeepsGradingShapeWithoutClaimingPublicationProvenance()
    {
        var quiz = SortingScenario.CreateQuiz();

        Assert.Equal(5, quiz.Questions.Length);
        Assert.Equal(5, quiz.Questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(quiz.Questions, question =>
        {
            Assert.Equal(4, question.Options.Length);
            Assert.Equal(4, question.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, question.Options.Select(option => option.Text).Distinct(StringComparer.Ordinal).Count());
            Assert.Single(question.Options.Where(option => option.Id == question.CorrectOptionId));
            Assert.Empty(question.SourceIds);
        });
        Assert.False(DomainRules.ValidateQuiz(quiz, []).IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void SyntheticAttemptRejectsImpossibleScores(int correctAnswers) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SortingScenario.CreateAttempt(correctAnswers));

    [Fact]
    public void ConstructorsRejectMissingPolicies()
    {
        Assert.Throws<ArgumentNullException>(() => new AfterSession(null!));
        Assert.Throws<ArgumentNullException>(() => new SortingFeedbackPolicy(null!));
    }

    [Theory]
    [InlineData("ocr-j277-sorting.json", "GCSE (OCR J277)", 60)]
    [InlineData("robot-sports-day.json", "Key Stage 3", 50)]
    public void LessonInputsUseOnlySupportedRequestFields(string fileName, string level, int duration)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lesson-inputs", fileName));
        using var document = JsonDocument.Parse(json);
        var fieldNames = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "DurationMinutes", "EducationalLevel", "Instructions", "Topic" }, fieldNames);

        var request = JsonSerializer.Deserialize<LessonRequest>(json, DomainRules.Json);
        Assert.NotNull(request);
        var validation = DomainRules.ValidateRequest(request);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.Equal(level, request.EducationalLevel);
        Assert.Equal(duration, request.DurationMinutes);
        Assert.False(string.IsNullOrWhiteSpace(request.Instructions));
    }

    private static AfterSession NewSession() => new(new SortingFeedbackPolicy(new ExactMatchGradingPolicy()));

    private static void AssertSameMarking(QuizResult expected, QuizResult actual)
    {
        Assert.Equal(expected.AttemptId, actual.AttemptId);
        Assert.Equal(expected.QuizId, actual.QuizId);
        Assert.Equal(expected.Score, actual.Score);
        Assert.Equal(expected.Total, actual.Total);
        Assert.Equal(expected.SubmittedAt, actual.SubmittedAt);
        Assert.Equal(expected.Questions.ToArray(), actual.Questions.ToArray());
    }

    private sealed class RecordingPolicy(QuizResult result) : IGradingPolicy
    {
        public int Calls { get; private set; }
        public QuizContent? SeenQuiz { get; private set; }
        public QuizAttempt? SeenAttempt { get; private set; }
        public DateTimeOffset? SeenSubmittedAt { get; private set; }

        public QuizResult Grade(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
        {
            Calls++;
            SeenQuiz = quiz;
            SeenAttempt = attempt;
            SeenSubmittedAt = submittedAt;
            return result;
        }
    }
}
