using System.Collections.Immutable;
using System.Text.Json;
using Azure.Core;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LessonPlanner.UnitTests;

// These labelled test doubles never perform network inference, retrieval or publication.
internal static class AgentTestData
{
    internal static readonly TimeProvider Clock = new FixedClock();
    internal static readonly PresenterIdentity Presenter = new(Guid.Parse("89d00eb9-fc63-4e45-b93b-ae53ba71f879"), Guid.Parse("e8f196ca-4a84-4671-a2c3-c06e9abf9b8a"));
    internal static readonly PublicationCaller Caller = new(Presenter, "d4b7e1e0-478b-4d0e-a7df-4d42767e6f09");
    internal static readonly LessonRequest Request = new("Sorting robot finishing times", "GCSE (OCR J277)", 60, null);
    internal static readonly SourceReference Source = new("test-curriculum", "Test curriculum evidence",
        new Uri("https://curriculum.example.test/specification.pdf"), Clock.GetUtcNow(), new string('a', 64),
        "Learners compare adjacent values when tracing bubble sort and explain when a sorted list requires no swaps.");
    internal static readonly OrchestrationPlan Plan = new([Source.Id], "Trace sorting with robot finishing times.", "Use a worked example followed by paired practice.");
    internal static readonly CurriculumBrief Brief = new("Learners trace comparisons and swaps in an age-appropriate sorting lesson.",
        ["Explain why adjacent values are compared when using bubble sort."], [Source]);
    internal static readonly ReviewResult Accepted = new(true, []);
    internal static readonly ReviewResult Revision = new(false, [new("accuracy", ReviewSeverity.Revision, "Clarify the comparison step before the next activity.")]);

    internal static LessonPlan Lesson() => new("Sorting robot finishing times",
        "Use robot finishing times to trace a small sorting example together.",
        [new("Trace adjacent comparisons and explain swaps.", [Source.Id])], ["Compare whole numbers."],
        "Bubble sort compares adjacent values and swaps out-of-order pairs. Repeat passes until a pass makes no swaps.",
        [new("Worked example", 30, "Compare neighbouring robot times and record each swap.", "Explain why the pair was swapped."),
            new("Paired practice", 30, "Trace another list with a partner and justify each comparison.", "Check the sorted list together.")],
        ["Which pair is compared first?", "When can another pass be omitted?"], ["Number cards"], [Source]);

    internal static QuizContent Quiz(int count = 5) => new("Robot sorting check", Enumerable.Range(1, count).Select(index =>
        new DraftQuestion($"q{index}", $"In sorting example {index}, what is the first bubble sort action?",
            [new("a", "Compare adjacent values"), new("b", "Discard every second value"), new("c", "Search a binary tree"), new("d", "Reverse the list without comparisons")],
            "a", "Bubble sort begins by comparing a neighbouring pair of values.", [Source.Id])).ToImmutableArray());

    internal static LessonRecord ReadyRecord() => new(DomainRules.NewId(), Presenter, Request, Lesson(), Accepted,
        RunStatus.Ready, [], Clock.GetUtcNow());
    internal static ApprovedPublication Approval() => new(DomainRules.NewId(), DomainRules.NewId(), 1, new string('b', 64));
    internal static PublishedQuiz Receipt() => new(DomainRules.NewId(), "Robot sorting check", new Uri("https://quiz.example.test/q/result"),
        Clock.GetUtcNow(), Clock.GetUtcNow().AddHours(1));
    internal static ChatResponse JsonResponse<T>(T value) => TextResponse(JsonSerializer.Serialize(value, AgentJson.Options));
    internal static ChatResponse TextResponse(string value) => new(new ChatMessage(ChatRole.Assistant, value));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }
}

internal sealed record TestAgentCreation(AgentRole Role, string Instructions, ChatOptions Options);
internal sealed record TestAgentRequest(AgentRole Role, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

internal sealed class ScriptedRoleProvider : IRoleAgentProvider
{
    internal List<TestAgentCreation> Creations { get; } = [];
    internal List<TestAgentRequest> Requests { get; } = [];
    internal Func<AgentRole, CancellationToken, Task<ChatResponse>> Respond { get; set; } =
        (_, _) => throw new InvalidOperationException("The test must supply a scripted response.");

    public AIAgent CreateAgent(AgentRole role, string instructions, ChatOptions options)
    {
        Creations.Add(new(role, instructions, options));
        var chatOptions = options.Clone();
        chatOptions.Instructions = instructions;
        var client = new ScriptedChatClient((messages, callOptions, token) =>
        {
            Requests.Add(new(role, messages, callOptions));
            return Respond(role, token);
        });
        return new ChatClientAgent(FoundryResponsesAgentProvider.ConfigureClient(client, options.Tools is { Count: > 0 }),
            new ChatClientAgentOptions { Name = $"Test{role}", ChatOptions = chatOptions, UseProvidedChatClientAsIs = true });
    }

    internal static ScriptedRoleProvider Returning<T>(T value) => new()
    {
        Respond = (_, _) => Task.FromResult(AgentTestData.JsonResponse(value))
    };

    private sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => respond(messages.ToArray(), options, cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}

internal sealed class TestRetriever : ICurriculumRetriever
{
    internal List<string> Calls { get; } = [];
    public IReadOnlyList<CurriculumSource> Sources { get; init; } =
        [new(AgentTestData.Source.Id, AgentTestData.Source.Title, AgentTestData.Source.Url)];
    internal Func<string, CancellationToken, Task<RetrievedSource>> Response { get; set; } =
        (_, _) => Task.FromResult(new RetrievedSource(AgentTestData.Source, "Retrieved full test content about adjacent comparisons and swaps."));
    public Task<RetrievedSource> RetrieveAsync(string sourceId, CancellationToken cancellationToken)
    {
        Calls.Add(sourceId);
        return Response(sourceId, cancellationToken);
    }
}

internal sealed class TestPublicationClient : IMcpPublicationClient
{
    private int _calls;
    internal int Calls => _calls;
    internal ApprovedPublication? Approval { get; private set; }
    internal PublicationCaller? Caller { get; private set; }
    internal Func<CancellationToken, Task<PublishedQuiz>> Response { get; set; } = _ => Task.FromResult(AgentTestData.Receipt());
    public Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Approval = approval;
        Caller = caller;
        return Response(cancellationToken);
    }
}

internal sealed class NoNetworkCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Token requests are forbidden in these tests.");
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Token requests are forbidden in these tests.");
}

internal sealed class TestActivitySink(Action<ActivityEvent>? onReport = null) : IProgress<ActivityEvent>
{
    internal List<ActivityEvent> Events { get; } = [];
    public void Report(ActivityEvent value)
    {
        Events.Add(value);
        onReport?.Invoke(value);
    }
}

internal sealed class AgentWorkflowHarness
{
    internal List<string> Calls { get; } = [];
    internal List<(LessonPlan? Previous, ReviewResult? Feedback)> LessonInputs { get; } = [];
    internal List<(QuizContent? Previous, ReviewResult? Feedback)> QuizInputs { get; } = [];
    internal TestRetriever Retriever { get; } = new();
    internal TestLessonStore Lessons { get; }
    internal TestQuizStore Quizzes { get; }
    internal Func<CancellationToken, Task<OrchestrationPlan>> Plan { get; set; } = _ => Task.FromResult(AgentTestData.Plan);
    internal Func<CancellationToken, Task<CurriculumBrief>> Curriculum { get; set; } = _ => Task.FromResult(AgentTestData.Brief);
    internal Func<int, CancellationToken, Task<LessonPlan>> Write { get; set; } = (_, _) => Task.FromResult(AgentTestData.Lesson());
    internal Func<int, CancellationToken, Task<ReviewResult>> LessonReview { get; set; } = (_, _) => Task.FromResult(AgentTestData.Accepted);
    internal Func<int, CancellationToken, Task<QuizContent>> Draft { get; set; } = (_, _) => Task.FromResult(AgentTestData.Quiz());
    internal Func<int, CancellationToken, Task<ReviewResult>> QuizReview { get; set; } = (_, _) => Task.FromResult(AgentTestData.Accepted);

    internal AgentWorkflowHarness()
    {
        Lessons = new(Calls);
        Quizzes = new(Calls);
    }

    internal LessonWorkflow Create(AgentExecutionLimits? limits = null) => new(new FakeOrchestrator(this), new FakeCurriculum(this),
        new FakeWriter(this), new FakeReviewer(this), new FakePublisher(this), Retriever, Lessons, Quizzes,
        limits ?? new(), AgentTestData.Clock);

    private sealed class FakeOrchestrator(AgentWorkflowHarness harness) : IOrchestratorAgent
    {
        public Task<OrchestrationPlan> PlanAsync(LessonRequest request, CancellationToken cancellationToken)
        { harness.Calls.Add("plan"); return harness.Plan(cancellationToken); }
    }
    private sealed class FakeCurriculum(AgentWorkflowHarness harness) : ICurriculumAgent
    {
        public Task<CurriculumBrief> AnalyseAsync(LessonRequest request, OrchestrationPlan plan, CancellationToken cancellationToken)
        { harness.Calls.Add("curriculum"); return harness.Curriculum(cancellationToken); }
    }
    private sealed class FakeWriter(AgentWorkflowHarness harness) : ILessonWriterAgent
    {
        public Task<LessonPlan> WriteAsync(LessonRequest request, CurriculumBrief curriculum, LessonPlan? previous,
            ReviewResult? feedback, CancellationToken cancellationToken)
        {
            harness.Calls.Add("write"); harness.LessonInputs.Add((previous, feedback));
            return harness.Write(harness.LessonInputs.Count, cancellationToken);
        }
    }
    private sealed class FakeReviewer(AgentWorkflowHarness harness) : IReviewerAgent
    {
        public Task<ReviewResult> ReviewLessonAsync(LessonRequest request, LessonPlan lesson, CancellationToken cancellationToken)
        { harness.Calls.Add("review-lesson"); return harness.LessonReview(harness.Calls.Count(call => call == "review-lesson"), cancellationToken); }
        public Task<ReviewResult> ReviewQuizAsync(LessonPlan lesson, QuizContent quiz, CancellationToken cancellationToken)
        { harness.Calls.Add("review-quiz"); return harness.QuizReview(harness.Calls.Count(call => call == "review-quiz"), cancellationToken); }
    }
    private sealed class FakePublisher(AgentWorkflowHarness harness) : IQuizPublisherAgent
    {
        public Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous, ReviewResult? feedback, CancellationToken cancellationToken)
        {
            harness.Calls.Add("draft"); harness.QuizInputs.Add((previous, feedback));
            return harness.Draft(harness.QuizInputs.Count, cancellationToken);
        }
        public Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Drafting must never publish.");
    }
}

internal sealed class TestLessonStore(List<string> calls) : ILessonStore
{
    internal List<LessonRecord> Writes { get; } = [];
    internal LessonRecord? Stored { get; set; }
    internal Exception? SaveFailure { get; set; }
    public Task SaveAsync(LessonRecord lesson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("save-lesson");
        if (SaveFailure is not null) throw SaveFailure;
        Writes.Add(lesson); Stored = lesson;
        return Task.CompletedTask;
    }
    public Task<LessonRecord?> FindAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); calls.Add("load-lesson"); return Task.FromResult(Stored); }
}

internal sealed class TestQuizStore(List<string> calls) : IQuizStore
{
    internal List<QuizDraft> Writes { get; } = [];
    internal Exception? SaveFailure { get; set; }
    public Task SaveDraftAsync(QuizDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("save-quiz");
        if (SaveFailure is not null) throw SaveFailure;
        Writes.Add(draft);
        return Task.CompletedTask;
    }
    public Task<QuizDraft?> FindDraftAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PublicationApproval> ApproveAsync(string draftId, PresenterIdentity presenter, string callerClientId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PublishedQuiz> PublishAsync(ApprovedPublication request, PublicationCaller caller, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PublicQuiz?> FindPublishedAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<QuizResult> SubmitAsync(QuizAttempt attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<QuizResult?> FindResultAsync(string quizId, string attemptId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
