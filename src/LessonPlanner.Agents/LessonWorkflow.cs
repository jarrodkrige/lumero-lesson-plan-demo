using System.Collections.Immutable;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

// The application owns sequencing and acceptance; no model can extend the revision loop or save a result.
public sealed class LessonWorkflow(IOrchestratorAgent orchestrator, ICurriculumAgent curriculum,
    ILessonWriterAgent writer, IReviewerAgent reviewer, IQuizPublisherAgent publisher,
    ICurriculumRetriever retriever, ILessonStore lessons, IQuizStore quizzes,
    AgentExecutionLimits limits, TimeProvider clock) : ILessonWorkflow
{
    public async Task<LessonRecord> GenerateAsync(LessonRequest request, PresenterIdentity presenter,
        IProgress<ActivityEvent>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(presenter);
        DomainRules.RequirePresenter(presenter);
        DomainRules.Require(DomainRules.ValidateRequest(request));
        limits.Validate();
        SortingWorkedExamples.ValidateInput(request);
        var trace = new ActivityTrace(DomainRules.NewId(), clock, progress);
        var createdAt = clock.GetUtcNow();
        LessonRecord record;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.WorkflowTimeout);
        try
        {
            var result = await GenerateCoreAsync(request, trace, deadline.Token).ConfigureAwait(false);
            record = new(trace.Id, presenter, request, result.Lesson, result.Review, result.Status,
                trace.Events, createdAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            trace.Emit(trace.ActiveRole, ActivityState.Failed, "Lesson generation exceeded its time budget. No lesson was accepted.");
            record = new(trace.Id, presenter, request, null, null, RunStatus.Failed, trace.Events, createdAt);
        }
        catch (Exception exception) when (exception is DomainException || AgentOperations.IsTransportFailure(exception))
        {
            trace.Emit(trace.ActiveRole, ActivityState.Failed, "Lesson generation could not be completed. No lesson was accepted.");
            record = new(trace.Id, presenter, request, null, null, RunStatus.Failed, trace.Events, createdAt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Final records are immutable. Persistence notifications are not pre-written as successful events.
        trace.NotifyPersistence(ActivityState.Started, "Saving the final lesson run.");
        try
        {
            await lessons.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DomainException || AgentOperations.IsTransportFailure(exception))
        {
            trace.NotifyPersistence(ActivityState.Failed, "The lesson run could not be saved.");
            throw new DomainException("lesson_storage", "The lesson run could not be saved.");
        }
        trace.NotifyPersistence(ActivityState.Succeeded, "The final lesson run was saved.");
        return record;
    }

    public async Task<QuizDraft> DraftQuizAsync(string lessonId, PresenterIdentity presenter,
        IProgress<ActivityEvent>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(presenter);
        DomainRules.RequirePresenter(presenter);
        DomainRules.RequireId(lessonId);
        limits.Validate();
        var trace = new ActivityTrace(DomainRules.NewId(), clock, progress);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.WorkflowTimeout);
        try
        {
            trace.Begin(AgentRole.Persistence, "Loading the presenter's accepted lesson.");
            var record = await lessons.FindAsync(lessonId, presenter, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (record is null || record.Owner != presenter || record.Id != lessonId)
                throw new DomainException("not_found", "The requested lesson is unavailable.");
            if (record.SchemaVersion != ContractVersions.Schema || record.Status != RunStatus.Ready ||
                record.Lesson is null || record.Review is null || !DomainRules.ReviewAccepted(record.Review))
                throw new DomainException("lesson_not_ready", "Only an accepted lesson can be used for a quiz.");
            AgentContractGuard.RequireReview(record.Review);
            DomainRules.Require(DomainRules.ValidateRequest(record.Request));
            AgentContractGuard.RequireSources(record.Lesson.Sources, clock);
            DomainRules.Require(AgentContractGuard.Lesson(record.Lesson, record.Request));
            trace.Emit(AgentRole.Persistence, ActivityState.Succeeded, "The saved lesson is accepted and available for quiz drafting.");

            var (content, review) = await DraftQuizCoreAsync(record.Lesson, trace, deadline.Token).ConfigureAwait(false);
            var draft = new QuizDraft(trace.Id, record.Id, 1, presenter, content, record.Lesson.Sources,
                review, clock.GetUtcNow(), string.Empty);
            draft = draft with { PayloadHash = DomainRules.DraftHash(draft) };
            trace.Begin(AgentRole.Persistence, "Saving the reviewed immutable quiz draft.");
            await quizzes.SaveDraftAsync(draft, deadline.Token).ConfigureAwait(false);
            trace.Emit(AgentRole.Persistence, ActivityState.Succeeded, "The reviewed immutable quiz draft was saved.");
            trace.Emit(AgentRole.QuizPublisher, ActivityState.AwaitingApproval, "Presenter approval is required before publication.");
            return draft;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            trace.Emit(trace.ActiveRole, ActivityState.Failed, "Quiz drafting exceeded its time budget. No publication was attempted.");
            throw new DomainException("quiz_timeout", "Quiz drafting exceeded its time budget.");
        }
        catch (DomainException exception)
        {
            trace.Emit(trace.ActiveRole, ActivityState.Failed, "Quiz drafting could not be completed. No publication was attempted.");
            throw new DomainException(exception.Code, "The quiz draft could not be completed. No publication was attempted.");
        }
        catch (Exception exception) when (AgentOperations.IsTransportFailure(exception))
        {
            trace.Emit(trace.ActiveRole, ActivityState.Failed, "Quiz drafting could not be completed. No publication was attempted.");
            throw new DomainException("quiz_unavailable", "The required quiz service is unavailable.");
        }
    }

    private async Task<LessonOutcome> GenerateCoreAsync(LessonRequest request, ActivityTrace trace,
        CancellationToken cancellationToken)
    {
        trace.Begin(AgentRole.Orchestrator, "Planning the lesson against the curriculum catalogue.");
        AgentContractGuard.RequireCatalogue(retriever.Sources);
        var catalogue = retriever.Sources.ToArray();
        var plan = await orchestrator.PlanAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        AgentContractGuard.RequirePlan(plan, catalogue);
        trace.Emit(AgentRole.Orchestrator, ActivityState.Succeeded, "The constrained curriculum source plan is ready.");

        trace.Begin(AgentRole.Curriculum, "Retrieving and analysing the selected curriculum sources.");
        var brief = await curriculum.AnalyseAsync(request, plan, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        AgentContractGuard.RequireCurriculum(brief, clock, plan);
        foreach (var source in brief.Sources)
            AgentContractGuard.RequireSourceBinding(source, catalogue.Single(entry => entry.Id == source.Id));
        trace.Emit(AgentRole.Curriculum, ActivityState.Succeeded, "Curriculum analysis and retrieved source provenance are ready.");

        LessonPlan? previous = null;
        ReviewResult? feedback = null;
        for (var revision = 0; revision <= ContractVersions.MaximumRevisions; revision++)
        {
            trace.Begin(AgentRole.LessonWriter, revision == 0 ? "Writing the initial lesson draft." : $"Writing lesson revision {revision} of two.");
            var candidate = await writer.WriteAsync(request, brief, previous, feedback, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null) throw AgentJson.InvalidOutput();
            candidate = candidate with { Sources = brief.Sources };
            trace.Emit(AgentRole.LessonWriter, ActivityState.Succeeded, "The lesson draft is ready for independent checks.");

            trace.Begin(AgentRole.Validation, "Checking lesson structure, timings and citation identifiers.");
            var validation = AgentContractGuard.Lesson(candidate, request);
            trace.Emit(AgentRole.Validation, validation.IsValid ? ActivityState.Succeeded : ActivityState.RevisionRequested,
                validation.IsValid ? "The deterministic lesson checks passed." : "The deterministic lesson checks require changes.");
            ReviewResult? review = null;
            if (validation.IsValid)
            {
                trace.Begin(AgentRole.Reviewer, "Independently reviewing the lesson and its evidence.");
                review = await reviewer.ReviewLessonAsync(request, candidate, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                AgentContractGuard.RequireReview(review);
                var accepted = DomainRules.ReviewAccepted(review);
                trace.Emit(AgentRole.Reviewer, accepted ? ActivityState.Succeeded : ActivityState.RevisionRequested,
                    accepted ? "The independent reviewer accepted the lesson." : "The independent reviewer requires lesson changes.");
                if (accepted) return new(candidate, review, RunStatus.Ready);
            }
            if (revision == ContractVersions.MaximumRevisions)
            {
                trace.Emit(AgentRole.Validation, ActivityState.Failed, "The lesson was not accepted within the two-revision limit.");
                return new(null, review, RunStatus.Rejected);
            }
            previous = candidate;
            feedback = Feedback(validation, review);
        }
        throw new InvalidOperationException("The bounded lesson loop did not terminate.");
    }

    private async Task<(QuizContent Content, ReviewResult Review)> DraftQuizCoreAsync(LessonPlan lesson,
        ActivityTrace trace, CancellationToken cancellationToken)
    {
        QuizContent? previous = null;
        ReviewResult? feedback = null;
        for (var revision = 0; revision <= ContractVersions.MaximumRevisions; revision++)
        {
            trace.Begin(AgentRole.QuizPublisher, revision == 0 ? "Drafting five multiple-choice questions." : $"Writing quiz revision {revision} of two.");
            var candidate = await publisher.DraftAsync(lesson, previous, feedback, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null) throw AgentJson.InvalidOutput();
            trace.Emit(AgentRole.QuizPublisher, ActivityState.Succeeded, "The quiz draft is ready for independent checks.");
            trace.Begin(AgentRole.Validation, "Checking the five-question structure, answer keys and citations.");
            var validation = AgentContractGuard.Quiz(candidate, lesson.Sources);
            trace.Emit(AgentRole.Validation, validation.IsValid ? ActivityState.Succeeded : ActivityState.RevisionRequested,
                validation.IsValid ? "The deterministic quiz checks passed." : "The deterministic quiz checks require changes.");
            ReviewResult? review = null;
            if (validation.IsValid)
            {
                trace.Begin(AgentRole.Reviewer, "Independently reviewing the quiz and every answer key.");
                review = await reviewer.ReviewQuizAsync(lesson, candidate, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                AgentContractGuard.RequireReview(review);
                var accepted = DomainRules.ReviewAccepted(review);
                trace.Emit(AgentRole.Reviewer, accepted ? ActivityState.Succeeded : ActivityState.RevisionRequested,
                    accepted ? "The independent reviewer accepted the quiz." : "The independent reviewer requires quiz changes.");
                if (accepted) return (candidate, review);
            }
            if (revision == ContractVersions.MaximumRevisions)
                throw new DomainException("quiz_rejected", "The quiz was not accepted within the two-revision limit.");
            previous = candidate;
            feedback = Feedback(validation, review);
        }
        throw new InvalidOperationException("The bounded quiz loop did not terminate.");
    }

    private static ReviewResult Feedback(ValidationResult validation, ReviewResult? review)
    {
        if (!validation.IsValid)
            return new(false, validation.Errors.Take(20).Select(error =>
                new ReviewFinding("deterministic_validation", ReviewSeverity.Revision, error)).ToImmutableArray());
        if (review is not null && !review.Findings.IsDefaultOrEmpty)
            return new(false, review.Findings);
        return new(false, [new("review_required", ReviewSeverity.Revision, "The independent reviewer did not accept the draft.")]);
    }

    private sealed record LessonOutcome(LessonPlan? Lesson, ReviewResult? Review, RunStatus Status);

    private sealed class ActivityTrace(string id, TimeProvider clock, IProgress<ActivityEvent>? progress)
    {
        private readonly ImmutableArray<ActivityEvent>.Builder _events = ImmutableArray.CreateBuilder<ActivityEvent>();
        internal string Id { get; } = id;
        internal AgentRole ActiveRole { get; private set; } = AgentRole.Orchestrator;
        internal ImmutableArray<ActivityEvent> Events => _events.ToImmutable();

        internal void Begin(AgentRole role, string summary)
        {
            ActiveRole = role;
            Emit(role, ActivityState.Started, summary);
        }

        internal void Emit(AgentRole role, ActivityState state, string summary)
        {
            var activity = new ActivityEvent(Id, clock.GetUtcNow(), role, state, summary);
            _events.Add(activity);
            Report(activity);
        }

        internal void NotifyPersistence(ActivityState state, string summary) =>
            Report(new ActivityEvent(Id, clock.GetUtcNow(), AgentRole.Persistence, state, summary));

        private void Report(ActivityEvent activity)
        {
            try
            {
                progress?.Report(activity);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
                // A disconnected observer cannot change acceptance; the safe lesson trace still belongs to the final record.
            }
        }
    }
}
