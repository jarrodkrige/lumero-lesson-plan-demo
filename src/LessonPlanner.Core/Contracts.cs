using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace LessonPlanner.Core;

public static class ContractVersions
{
    public const string Schema = "lumero.v1";
    public const string PublishAction = "publish_quiz";
    public const int QuestionCount = 5;
    public const int MaximumRevisions = 2;
}

public sealed record PresenterIdentity(Guid TenantId, Guid ObjectId);
public sealed record PublicationCaller(PresenterIdentity Presenter, string ClientId);
public sealed record LessonRequest(string Topic, string EducationalLevel, int DurationMinutes, string? Instructions);
public sealed record SourceReference(string Id, string Title, Uri Url, DateTimeOffset RetrievedAt, string Sha256, string Extract);
public sealed record CurriculumSource(string Id, string Title, Uri Url);
public sealed record RetrievedSource(SourceReference Reference, string Content);
public sealed record OrchestrationPlan(ImmutableArray<string> SourceIds, string LearningFocus, string TeachingApproach);
public sealed record CurriculumBrief(string Summary, ImmutableArray<string> Requirements, ImmutableArray<SourceReference> Sources);
public sealed record LearningObjective(string Description, ImmutableArray<string> SourceIds);
public sealed record LessonActivity(string Title, int DurationMinutes, string Instructions, string Assessment);
public sealed record LessonPlan(string Title, string Overview, ImmutableArray<LearningObjective> Objectives,
    ImmutableArray<string> Prerequisites, string Explanation, ImmutableArray<LessonActivity> Activities,
    ImmutableArray<string> AssessmentQuestions, ImmutableArray<string> Materials, ImmutableArray<SourceReference> Sources);
public enum ReviewSeverity { Information, Revision, Critical }
public sealed record ReviewFinding(string Code, ReviewSeverity Severity, string Message);
public sealed record ReviewResult(bool Accepted, ImmutableArray<ReviewFinding> Findings);
public sealed record ValidationResult(ImmutableArray<string> Errors)
{
    public bool IsValid => Errors.IsDefaultOrEmpty;
    public static ValidationResult Success { get; } = new([]);
}
public enum AgentRole { Orchestrator, Curriculum, LessonWriter, Reviewer, QuizPublisher, Validation, Persistence }
public enum ActivityState { Started, Succeeded, Failed, RevisionRequested, AwaitingApproval }
public sealed record ActivityEvent(string CorrelationId, DateTimeOffset Timestamp, AgentRole Role, ActivityState State, string Summary);
public enum RunStatus { Ready, Rejected, Failed }
public sealed record LessonRecord(string Id, PresenterIdentity Owner, LessonRequest Request, LessonPlan? Lesson,
    ReviewResult? Review, RunStatus Status, ImmutableArray<ActivityEvent> Activities, DateTimeOffset CreatedAt, string SchemaVersion = ContractVersions.Schema);

public sealed record QuizOption(string Id, string Text);
public sealed record DraftQuestion(string Id, string Prompt, ImmutableArray<QuizOption> Options, string CorrectOptionId,
    string Explanation, ImmutableArray<string> SourceIds);
public sealed record QuizContent(string Title, ImmutableArray<DraftQuestion> Questions);
public sealed record QuizDraft(string Id, string LessonId, int Version, PresenterIdentity Owner, QuizContent Content,
    ImmutableArray<SourceReference> Sources, ReviewResult Review, DateTimeOffset CreatedAt, string PayloadHash,
    string SchemaVersion = ContractVersions.Schema);
public sealed record PublicationApproval(string Id, string DraftId, int Version, string PayloadHash,
    PresenterIdentity Presenter, string CallerClientId, string Action, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record ApprovedPublication(string ApprovalId, string DraftId, int Version, string PayloadHash);
public sealed record PublishedQuiz(string Id, string Title, Uri PublicUrl, DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt);

// The anonymous read contract deliberately has no marking key or explanation.
public sealed record PublicQuestion(string Id, string Prompt, ImmutableArray<QuizOption> Options);
public sealed record PublicQuiz(string Id, string Title, ImmutableArray<PublicQuestion> Questions, DateTimeOffset ExpiresAt)
{
    public ImmutableArray<CurriculumSource> AttributedSources { get; init; } = [];
}
public sealed record QuizAnswer(string QuestionId, string OptionId);
public sealed record QuizAttempt(string Id, string QuizId, ImmutableArray<QuizAnswer> Answers);
public sealed record MarkedQuestion(string QuestionId, string Prompt, string SelectedOptionId, string CorrectOptionId,
    bool IsCorrect, string Explanation);
public sealed record QuizResult(string AttemptId, string QuizId, int Score, int Total, string Feedback,
    ImmutableArray<MarkedQuestion> Questions, DateTimeOffset SubmittedAt)
{
    [JsonIgnore]
    public ImmutableArray<CurriculumSource> AttributedSources { get; init; } = [];
}
public sealed record LessonDocument(string FileName, string ContentType, byte[] Content);
