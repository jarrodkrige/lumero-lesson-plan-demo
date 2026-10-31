using System.Collections.Immutable;
using System.Text.Json;
using Azure.Identity;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace LessonPlanner.UnitTests;

public sealed class CoreSafetyTests
{
    private static readonly ImmutableArray<SourceReference> Sources = [new("sorting", "Synthetic test source",
        new Uri("https://www.ocr.org.uk/qualifications/gcse/computer-science-j277-from-2020/"), DateTimeOffset.UtcNow,
        new string('a', 64), "Synthetic sorting curriculum text used only for a deterministic unit test.")];
    private static QuizContent Quiz() => new("Sorting knowledge check", Enumerable.Range(1, 5).Select(number =>
        new DraftQuestion($"q{number}", $"Which comparison is correct in question {number}?",
            [new("a", "Compare neighbouring values"), new("b", "Discard every value"), new("c", "Ignore the sequence"), new("d", "Choose no value")],
            "a", "A comparison examines neighbouring values in this synthetic example.", ["sorting"])).ToImmutableArray());
    private static QuizAttempt Attempt(QuizContent quiz, int correct = 5) => new(DomainRules.NewId(), DomainRules.NewId(),
        quiz.Questions.Select((question, index) => new QuizAnswer(question.Id, index < correct ? "a" : "b")).ToImmutableArray());

    [Fact]
    public void NullStructuredOutputsFailValidation()
    {
        Assert.False(DomainRules.ValidateRequest(null).IsValid);
        Assert.False(DomainRules.ValidateLesson(null, new("Sorting algorithms", "GCSE (OCR J277)", 60, null)).IsValid);
        Assert.False(DomainRules.ValidateQuiz(null, Sources).IsValid);
        Assert.False(DomainRules.ValidateReview(null).IsValid);
        Assert.False(DomainRules.ReviewAccepted(null));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"topic\":null,\"educationalLevel\":\"GCSE (OCR J277)\",\"durationMinutes\":60,\"instructions\":null}")]
    [InlineData("{\"topic\":\"Sorting\",\"educationalLevel\":\"GCSE (OCR J277)\",\"durationMinutes\":60,\"instructions\":null,\"approved\":true}")]
    public void StrictJsonRejectsMissingNullAndUnrecognisedMembers(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LessonRequest>(json, DomainRules.Json));

    [Fact]
    public void NullQuestionsAndOptionsFailWithoutDereferencingThem()
    {
        var quiz = Quiz();
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.SetItem(0, null!) }, Sources).IsValid);
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.SetItem(0,
            quiz.Questions[0] with { Options = [null!, new("b", "Example"), new("c", "Example"), new("d", "Example")] }) }, Sources).IsValid);
    }

    [Fact]
    public void QuizRequiresExactlyFiveQuestionsWithDistinctIds()
    {
        var quiz = Quiz();
        Assert.True(DomainRules.ValidateQuiz(quiz, Sources).IsValid);
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.RemoveAt(0) }, Sources).IsValid);
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.Add(quiz.Questions[0]) }, Sources).IsValid);
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.SetItem(1, quiz.Questions[0]) }, Sources).IsValid);
    }

    [Fact]
    public void QuizRejectsUnknownMarkingOptionAndMissingProvenance()
    {
        var quiz = Quiz();
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.SetItem(0,
            quiz.Questions[0] with { CorrectOptionId = "injected" }) }, Sources).IsValid);
        Assert.False(DomainRules.ValidateQuiz(quiz with { Questions = quiz.Questions.SetItem(0,
            quiz.Questions[0] with { SourceIds = ["unretrieved"] }) }, Sources).IsValid);
    }

    [Fact]
    public void ReviewAcceptanceRequiresValidInformationalFindingsOnly()
    {
        Assert.True(DomainRules.ReviewAccepted(new(true, [])));
        Assert.True(DomainRules.ReviewAccepted(new(true, [new("checked", ReviewSeverity.Information, "All required checks passed.")])));
        Assert.False(DomainRules.ReviewAccepted(new(false, [])));
        Assert.False(DomainRules.ReviewAccepted(new(true, [new("revise", ReviewSeverity.Revision, "Revise this question before publication.")])));
        Assert.False(DomainRules.ReviewAccepted(new(true, [new("invalid", (ReviewSeverity)42, "An unknown severity cannot approve work.")])));
        Assert.False(DomainRules.ReviewAccepted(new(true, [null!])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void GradingCountsOnlyServerOwnedCorrectOptions(int correct)
    {
        var quiz = Quiz();
        var result = new ExactMatchGradingPolicy().Grade(quiz, Attempt(quiz, correct), DateTimeOffset.UtcNow);
        Assert.Equal(correct, result.Score);
        Assert.Equal(5, result.Total);
        Assert.Equal(correct, result.Questions.Count(question => question.IsCorrect));
        Assert.All(result.Questions, question => Assert.Equal("a", question.CorrectOptionId));
    }

    [Fact]
    public void GradingRejectsDuplicateMissingNullAndUnknownAnswers()
    {
        var quiz = Quiz();
        var attempt = Attempt(quiz);
        var grader = new ExactMatchGradingPolicy();
        Assert.Throws<DomainException>(() => grader.Grade(quiz, attempt with { Answers = attempt.Answers.RemoveAt(0) }, DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => grader.Grade(quiz, attempt with { Answers = attempt.Answers.SetItem(1, attempt.Answers[0]) }, DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => grader.Grade(quiz, attempt with { Answers = attempt.Answers.SetItem(0, null!) }, DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => grader.Grade(quiz, attempt with { Answers = attempt.Answers.SetItem(0, new("q1", "unknown")) }, DateTimeOffset.UtcNow));
        Assert.Throws<DomainException>(() => grader.Grade(quiz, attempt with { Answers = attempt.Answers.SetItem(0, new(null!, "a")) }, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("http://public.example.org")]
    [InlineData("https://example.invalid")]
    [InlineData("https://example.example")]
    [InlineData("https://user:password@example.org")]
    [InlineData("https://example.org/quiz")]
    [InlineData("https://example.org/?target=other")]
    [InlineData("https://example.org/#fragment")]
    public void PublicOriginRejectsUnsafeOrPlaceholderAddresses(string address) => Assert.Throws<InvalidOperationException>(() =>
        new QuizPolicy(new Uri(address), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 200).Validate(false));

    [Fact]
    public void LoopbackHttpRequiresExplicitLocalEnvironment()
    {
        var policy = new QuizPolicy(new Uri("http://127.0.0.1:5180"), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 200);
        Assert.Throws<InvalidOperationException>(() => policy.Validate(false));
        policy.Validate(true);
        Assert.Equal(new Uri($"http://127.0.0.1:5180/quiz/{new string('a', 48)}"), policy.PublicUrl(new string('a', 48)));
        Assert.Throws<DomainException>(() => policy.PublicUrl("../other"));
    }

    [Theory]
    [InlineData("UseDevelopmentStorage=true")]
    [InlineData("AccountName=realaccount;AccountKey=not-a-key;TableEndpoint=https://realaccount.table.core.windows.net;")]
    public void ProductionRejectsAllStorageConnectionStrings(string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:ConnectionString"] = connection }).Build();
        Assert.Throws<InvalidOperationException>(() => InfrastructureRegistration.CreateTableClient(configuration, false));
    }

    [Theory]
    [InlineData("AccountName=devstoreaccount1;AccountKey=not-a-key;TableEndpoint=https://remote.example.org;")]
    [InlineData("AccountName=devstoreaccount1;AccountKey=not-a-key;TableEndpoint=http://127.0.0.1:11002;TableEndpoint=https://remote.example.org;")]
    [InlineData("UseDevelopmentStorage=true;DevelopmentStorageProxyUri=https://remote.example.org;")]
    [InlineData("AccountName=devstoreaccount1;AccountKey=not-a-key;TableEndpoint=http://127.0.0.1:11002;EndpointSuffix=example.org;")]
    public void LocalStorageConnectionCannotEscapeLoopback(string connection) =>
        Assert.False(InfrastructureRegistration.IsLoopbackStorageConnection(connection));

    [Fact]
    public void CredentialSelectionHasNoHiddenFallbackChain()
    {
        Assert.IsType<ManagedIdentityCredential>(InfrastructureRegistration.CreateCredential(null, false));
        Assert.IsType<AzureCliCredential>(InfrastructureRegistration.CreateCredential(null, true));
    }

    [Theory]
    [InlineData("http://remote.example.org/mcp", "api://test/.default")]
    [InlineData("https://remote.example.org/mcp", "https://ai.azure.com/")]
    [InlineData("https://remote.example.org/mcp?token=invalid", "api://test/.default")]
    public void McpConfigurationRequiresItsOwnSecureTransportAndScope(string endpoint, string scope) =>
        Assert.Throws<InvalidOperationException>(() => new McpConnectionOptions(new Uri(endpoint), scope).Validate(true));
}
