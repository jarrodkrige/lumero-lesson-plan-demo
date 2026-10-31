namespace LessonPlanner.Core;

public interface IOrchestratorAgent
{
    Task<OrchestrationPlan> PlanAsync(LessonRequest request, CancellationToken cancellationToken);
}
public interface ICurriculumAgent
{
    Task<CurriculumBrief> AnalyseAsync(LessonRequest request, OrchestrationPlan plan, CancellationToken cancellationToken);
}
public interface ILessonWriterAgent
{
    Task<LessonPlan> WriteAsync(LessonRequest request, CurriculumBrief curriculum, LessonPlan? previous,
        ReviewResult? feedback, CancellationToken cancellationToken);
}
public interface IReviewerAgent
{
    Task<ReviewResult> ReviewLessonAsync(LessonRequest request, LessonPlan lesson, CancellationToken cancellationToken);
    Task<ReviewResult> ReviewQuizAsync(LessonPlan lesson, QuizContent quiz, CancellationToken cancellationToken);
}
public interface IQuizPublisherAgent
{
    Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous, ReviewResult? feedback, CancellationToken cancellationToken);
    Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller, CancellationToken cancellationToken);
}
public interface ICurriculumRetriever
{
    IReadOnlyList<CurriculumSource> Sources { get; }
    Task<RetrievedSource> RetrieveAsync(string sourceId, CancellationToken cancellationToken);
}
public interface ILessonWorkflow
{
    Task<LessonRecord> GenerateAsync(LessonRequest request, PresenterIdentity presenter,
        IProgress<ActivityEvent>? progress, CancellationToken cancellationToken);
    Task<QuizDraft> DraftQuizAsync(string lessonId, PresenterIdentity presenter,
        IProgress<ActivityEvent>? progress, CancellationToken cancellationToken);
}
public interface ILessonStore
{
    Task SaveAsync(LessonRecord lesson, CancellationToken cancellationToken);
    Task<LessonRecord?> FindAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken);
}
public interface IQuizStore
{
    Task SaveDraftAsync(QuizDraft draft, CancellationToken cancellationToken);
    Task<QuizDraft?> FindDraftAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken);
    Task<PublicationApproval> ApproveAsync(string draftId, PresenterIdentity presenter, string callerClientId, CancellationToken cancellationToken);
    Task<PublishedQuiz> PublishAsync(ApprovedPublication request, PublicationCaller caller, CancellationToken cancellationToken);
    Task<PublicQuiz?> FindPublishedAsync(string id, CancellationToken cancellationToken);
    Task<QuizResult> SubmitAsync(QuizAttempt attempt, CancellationToken cancellationToken);
    Task<QuizResult?> FindResultAsync(string quizId, string attemptId, CancellationToken cancellationToken);
}
public interface IMcpPublicationClient
{
    Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller, CancellationToken cancellationToken);
}
public interface IGradingPolicy
{
    QuizResult Grade(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt);
}
public interface ILessonDocumentRenderer
{
    LessonDocument Render(LessonRecord lesson);
}
public interface IQuizQrRenderer
{
    byte[] Render(Uri publicUrl);
}
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
