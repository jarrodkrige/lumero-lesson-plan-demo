using System.Collections.Immutable;
using LessonPlanner.Core;

namespace LessonPlanner.IntegrationTests;

internal static class TestData
{
    public static readonly PresenterIdentity Presenter = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"));
    public const string ClientId = "33333333-3333-4333-8333-333333333333";
    public const string Audience = "44444444-4444-4444-8444-444444444444";
    public static readonly ImmutableArray<SourceReference> Sources = [new("ocr-j277", "Synthetic curriculum source for local tests",
        new Uri("https://www.ocr.org.uk/qualifications/gcse/computer-science-j277-from-2020/"),
        new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), new string('a', 64),
        "Synthetic local-test provenance. No claim of live retrieval is made.")];

    public static LessonRecord Lesson(PresenterIdentity? owner = null)
    {
        var request = new LessonRequest("Sorting algorithms", "GCSE (OCR J277)", 60, null);
        var plan = new LessonPlan("Tracing sorting algorithms", "Trace short lists to explain how sorting algorithms compare and move values.",
            [new("Trace bubble sort on a short list of integers.", ["ocr-j277"])],
            ["Compare two integers."],
            "Compare each adjacent pair and swap them when they are out of order. Repeat passes until no swaps are needed.",
            [new("Trace a sorting pass", 60, "Work in pairs to trace adjacent comparisons on paper, recording each swap.", "Explain which value is in its final position.")],
            ["What is compared during a pass?", "When may bubble sort stop?"], ["Number cards", "Paper"], Sources);
        return new(DomainRules.NewId(), owner ?? Presenter, request, plan, new(true, []), RunStatus.Ready, [], DateTimeOffset.UtcNow);
    }

    public static QuizDraft Draft(LessonRecord lesson)
    {
        var questions = Enumerable.Range(1, ContractVersions.QuestionCount).Select(index => new DraftQuestion($"q{index}",
            $"For example {index}, what does one adjacent comparison examine?",
            [new("a", "Two neighbouring values"), new("b", "Only the first value"),
                new("c", "Every possible pair at once"), new("d", "Only the last value")],
            "a", "The private marking explanation identifies the two neighbouring values being compared.", ["ocr-j277"])).ToImmutableArray();
        var draft = new QuizDraft(DomainRules.NewId(), lesson.Id, 1, lesson.Owner,
            new("Five sorting questions", questions), Sources, new(true, []), DateTimeOffset.UtcNow, "");
        return draft with { PayloadHash = DomainRules.DraftHash(draft) };
    }

    public static QuizAttempt Attempt(QuizDraft draft, string option = "a") => new(DomainRules.NewId(), draft.Id,
        draft.Content.Questions.Select(q => new QuizAnswer(q.Id, option)).ToImmutableArray());

    public static ApprovedPublication Approved(PublicationApproval approval) => new(approval.Id, approval.DraftId, approval.Version, approval.PayloadHash);
}
