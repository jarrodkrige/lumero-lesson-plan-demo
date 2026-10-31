using System.Collections.Immutable;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure;

public sealed class AzureTableStore(TableClient table, QuizPolicy policy, IGradingPolicy grading, TimeProvider clock)
    : ILessonStore, IQuizStore
{
    private const int MaximumRecordCharacters = 30000;
    private const int MaximumContentionRetries = 8;

    public async Task SaveAsync(LessonRecord lesson, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(lesson.Id);
        DomainRules.RequirePresenter(lesson.Owner);
        DomainRules.Require(DomainRules.ValidateRequest(lesson.Request));
        if (lesson.SchemaVersion != ContractVersions.Schema || !Enum.IsDefined(lesson.Status))
            throw InvalidRecord();
        if (lesson.Status == RunStatus.Ready)
        {
            if (lesson.Lesson is null || lesson.Review is null || !DomainRules.ReviewAccepted(lesson.Review))
                throw InvalidRecord();
            DomainRules.Require(DomainRules.ValidateLesson(lesson.Lesson, lesson.Request));
        }
        if (lesson.Activities.IsDefault || lesson.Activities.Length > 80 || lesson.Activities.Any(activity =>
                activity is null || activity.CorrelationId != lesson.Id || !Enum.IsDefined(activity.Role) ||
                !Enum.IsDefined(activity.State) || string.IsNullOrWhiteSpace(activity.Summary) || activity.Summary.Length > 300))
            throw InvalidRecord();
        var entity = Entity($"lesson-{lesson.Id}", "lesson", lesson);
        await AddImmutableAsync(entity, cancellationToken);
    }

    public async Task<LessonRecord?> FindAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(id);
        DomainRules.RequirePresenter(presenter);
        var row = await GetAsync($"lesson-{id}", "lesson", cancellationToken);
        if (row is null) return null;
        var record = Read<LessonRecord>(row);
        return record.Owner == presenter && record.SchemaVersion == ContractVersions.Schema ? record : null;
    }

    public async Task SaveDraftAsync(QuizDraft draft, CancellationToken cancellationToken)
    {
        ValidateDraft(draft);
        var lesson = await FindAsync(draft.LessonId, draft.Owner, cancellationToken);
        if (lesson?.Status != RunStatus.Ready || lesson.Lesson is null ||
            DomainRules.Hash(lesson.Lesson.Sources) != DomainRules.Hash(draft.Sources))
            throw new DomainException("draft_provenance", "A quiz must refer to its presenter's reviewed lesson and retrieved sources.");
        var row = Entity(Partition(draft.Id), "draft", draft);
        await AddImmutableAsync(row, cancellationToken);
    }

    public async Task<QuizDraft?> FindDraftAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(id);
        DomainRules.RequirePresenter(presenter);
        var row = await GetAsync(Partition(id), "draft", cancellationToken);
        if (row is null) return null;
        var draft = Read<QuizDraft>(row);
        if (draft.Owner != presenter) return null;
        ValidateDraft(draft);
        return draft;
    }

    public async Task<PublicationApproval> ApproveAsync(string draftId, PresenterIdentity presenter,
        string callerClientId, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(draftId);
        DomainRules.RequirePresenter(presenter);
        if (!Guid.TryParse(callerClientId, out var callerId) || callerId == Guid.Empty)
            throw new DomainException("forbidden", "The publication service identity is not configured.");
        var draft = await FindDraftAsync(draftId, presenter, cancellationToken) ?? throw NotFound();
        var partition = Partition(draftId);
        for (var retry = 0; retry < MaximumContentionRetries; retry++)
        {
            var now = clock.GetUtcNow();
            var current = await GetAsync(partition, "approval", cancellationToken);
            if (current is not null)
            {
                var existing = Read<PublicationApproval>(current);
                if (existing.Presenter != presenter || !SameClient(existing.CallerClientId, callerClientId))
                    throw new DomainException("forbidden", "This draft is bound to a different publication identity.");
                if (existing.ExpiresAt > now)
                    return existing;
            }
            if (await GetAsync(partition, "publication", cancellationToken) is not null)
                throw new DomainException("already_published", "This quiz has already been published.");
            var approval = new PublicationApproval(DomainRules.NewId(), draftId, draft.Version,
                draft.PayloadHash, presenter, callerId.ToString("D"), ContractVersions.PublishAction,
                now, now.Add(policy.ApprovalLifetime));
            var entity = Entity(partition, "approval", approval);
            entity["Consumed"] = false;
            try
            {
                if (current is null) await table.AddEntityAsync(entity, cancellationToken);
                else await table.UpdateEntityAsync(entity, current.ETag, TableUpdateMode.Replace, cancellationToken);
                return approval;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // Another approval request won the conditional write. Read its authoritative state.
            }
        }
        throw Busy();
    }

    public async Task<PublishedQuiz> PublishAsync(ApprovedPublication request, PublicationCaller caller,
        CancellationToken cancellationToken)
    {
        DomainRules.RequireId(request.DraftId);
        DomainRules.RequireId(request.ApprovalId);
        DomainRules.RequirePresenter(caller.Presenter);
        var partition = Partition(request.DraftId);
        var draftRow = await GetAsync(partition, "draft", cancellationToken) ?? throw NotFound();
        var draft = Read<QuizDraft>(draftRow);
        ValidateDraft(draft);
        if (draft.Owner != caller.Presenter) throw ForbiddenApproval();
        for (var retry = 0; retry < MaximumContentionRetries; retry++)
        {
            var approvalRow = await GetAsync(partition, "approval", cancellationToken) ?? throw ForbiddenApproval();
            var approval = Read<PublicationApproval>(approvalRow);
            ValidateApproval(approval, request, draft, caller, clock.GetUtcNow());
            var existing = await GetAsync(partition, "publication", cancellationToken);
            var now = clock.GetUtcNow();
            ValidateApproval(approval, request, draft, caller, now);
            if (existing is not null)
            {
                if (existing.GetString("ApprovalId") != request.ApprovalId) throw ForbiddenApproval();
                // The earlier approval read may predate another caller's atomic publication.
                if (approvalRow.GetBoolean("Consumed") != true) continue;
                return Read<PublishedQuiz>(existing);
            }
            if (approvalRow.GetBoolean("Consumed") == true) throw InvalidRecord();
            var receipt = new PublishedQuiz(draft.Id, draft.Content.Title, policy.PublicUrl(draft.Id), now,
                now.Add(policy.Lifetime));
            var publication = Entity(partition, "publication", receipt);
            publication["ApprovalId"] = approval.Id;
            publication["SubmissionCount"] = 0;
            publication["MaximumAttempts"] = policy.MaximumAttempts;
            approvalRow["Consumed"] = true;
            approvalRow["ConsumedAt"] = now;
            try
            {
                // Azure Tables guarantees a transaction only within this table and this partition.
                await table.SubmitTransactionAsync([
                    new(TableTransactionActionType.UpdateReplace, approvalRow, approvalRow.ETag),
                    new(TableTransactionActionType.Add, publication)
                ], cancellationToken);
                return receipt;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // A concurrent or retried request can return only the receipt for its own approval.
            }
        }
        throw Busy();
    }

    public async Task<PublicQuiz?> FindPublishedAsync(string id, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(id);
        var partition = Partition(id);
        var publication = await GetAsync(partition, "publication", cancellationToken);
        if (publication is null) return null;
        var receipt = Read<PublishedQuiz>(publication);
        if (receipt.ExpiresAt <= clock.GetUtcNow()) return null;
        var draftRow = await GetAsync(partition, "draft", cancellationToken) ?? throw InvalidRecord();
        var draft = Read<QuizDraft>(draftRow);
        ValidateDraft(draft);
        return new(receipt.Id, receipt.Title, draft.Content.Questions.Select(q =>
            new PublicQuestion(q.Id, q.Prompt, q.Options)).ToImmutableArray(), receipt.ExpiresAt);
    }

    public async Task<QuizResult> SubmitAsync(QuizAttempt attempt, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(attempt.Id);
        DomainRules.RequireId(attempt.QuizId);
        if (attempt.Answers.IsDefault || attempt.Answers.Length != ContractVersions.QuestionCount ||
            attempt.Answers.Any(a => a is null || a.QuestionId is null || a.QuestionId.Length > 32 ||
                a.OptionId is null || a.OptionId.Length > 16))
            throw new DomainException("invalid_answers", "Select one option for every question.");
        var partition = Partition(attempt.QuizId);
        var initialPublication = await GetAsync(partition, "publication", cancellationToken) ?? throw NotFound();
        if (Read<PublishedQuiz>(initialPublication).ExpiresAt <= clock.GetUtcNow())
            throw new DomainException("quiz_closed", "This quiz is no longer accepting answers.");
        var draftRow = await GetAsync(partition, "draft", cancellationToken) ?? throw NotFound();
        var draft = Read<QuizDraft>(draftRow);
        ValidateDraft(draft);
        var attemptHash = DomainRules.Hash(attempt);
        var result = grading.Grade(draft.Content, attempt, clock.GetUtcNow());
        for (var retry = 0; retry < MaximumContentionRetries; retry++)
        {
            var publication = await GetAsync(partition, "publication", cancellationToken) ?? throw NotFound();
            var receipt = Read<PublishedQuiz>(publication);
            if (receipt.ExpiresAt <= clock.GetUtcNow())
                throw new DomainException("quiz_closed", "This quiz is no longer accepting answers.");
            var existing = await GetAsync(partition, $"attempt-{attempt.Id}", cancellationToken);
            if (existing is not null)
            {
                if (existing.GetString("AnswerHash") != attemptHash)
                    throw new DomainException("attempt_conflict", "This submission has already been recorded with different answers.");
                return Read<QuizResult>(existing);
            }
            var count = publication.GetInt32("SubmissionCount") ?? throw InvalidRecord();
            var maximum = publication.GetInt32("MaximumAttempts") ?? throw InvalidRecord();
            if (count < 0 || maximum is < 1 or > 1000 || count > maximum) throw InvalidRecord();
            if (count >= maximum) throw new DomainException("quiz_full", "This quiz has reached its submission limit.");
            var attemptRow = Entity(partition, $"attempt-{attempt.Id}", result);
            attemptRow["AnswerHash"] = attemptHash;
            publication["SubmissionCount"] = checked(count + 1);
            try
            {
                await table.SubmitTransactionAsync([
                    new(TableTransactionActionType.UpdateReplace, publication, publication.ETag),
                    new(TableTransactionActionType.Add, attemptRow)
                ], cancellationToken);
                return result;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                // Counting and the result are retried together, never written independently.
            }
        }
        throw Busy();
    }

    public async Task<QuizResult?> FindResultAsync(string quizId, string attemptId, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(quizId);
        DomainRules.RequireId(attemptId);
        var partition = Partition(quizId);
        var publication = await GetAsync(partition, "publication", cancellationToken);
        if (publication is null || Read<PublishedQuiz>(publication).ExpiresAt <= clock.GetUtcNow()) return null;
        var result = await GetAsync(partition, $"attempt-{attemptId}", cancellationToken);
        return result is null ? null : Read<QuizResult>(result);
    }

    private async Task AddImmutableAsync(TableEntity entity, CancellationToken cancellationToken)
    {
        try { await table.AddEntityAsync(entity, cancellationToken); }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            var existing = await GetAsync(entity.PartitionKey, entity.RowKey, cancellationToken);
            if (existing?.GetString("PayloadHash") != entity.GetString("PayloadHash"))
                throw new DomainException("immutable_conflict", "An immutable record cannot be overwritten.");
        }
    }

    private async Task<TableEntity?> GetAsync(string partition, string row, CancellationToken cancellationToken)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(partition, row, cancellationToken: cancellationToken);
        return response.HasValue ? response.Value : null;
    }

    private static TableEntity Entity<T>(string partition, string row, T payload)
    {
        var json = JsonSerializer.Serialize(payload, DomainRules.Json);
        if (json.Length > MaximumRecordCharacters) throw new DomainException("record_too_large", "The record exceeds the storage budget.");
        return new(partition, row) { ["Payload"] = json, ["PayloadHash"] = DomainRules.Hash(payload), ["Schema"] = ContractVersions.Schema };
    }

    private static T Read<T>(TableEntity row)
    {
        if (row.GetString("Schema") != ContractVersions.Schema) throw InvalidRecord();
        var payload = row.GetString("Payload");
        if (payload is null || payload.Length > MaximumRecordCharacters) throw InvalidRecord();
        try
        {
            var value = JsonSerializer.Deserialize<T>(payload, DomainRules.Json) ?? throw InvalidRecord();
            if (DomainRules.Hash(value) != row.GetString("PayloadHash")) throw InvalidRecord();
            return value;
        }
        catch (JsonException) { throw InvalidRecord(); }
    }

    private static void ValidateDraft(QuizDraft draft)
    {
        DomainRules.RequireId(draft.Id);
        DomainRules.RequireId(draft.LessonId);
        DomainRules.RequirePresenter(draft.Owner);
        if (draft.Version is < 1 or > 50 || draft.SchemaVersion != ContractVersions.Schema ||
            !DomainRules.ReviewAccepted(draft.Review) || draft.PayloadHash != DomainRules.DraftHash(draft))
            throw InvalidRecord();
        DomainRules.Require(DomainRules.ValidateQuiz(draft.Content, draft.Sources));
    }

    private static void ValidateApproval(PublicationApproval approval, ApprovedPublication request,
        QuizDraft draft, PublicationCaller caller, DateTimeOffset now)
    {
        if (approval.Id != request.ApprovalId || approval.DraftId != draft.Id || approval.Action != ContractVersions.PublishAction ||
            approval.Version != request.Version || draft.Version != request.Version ||
            approval.PayloadHash != request.PayloadHash || draft.PayloadHash != request.PayloadHash ||
            approval.Presenter != caller.Presenter || !SameClient(approval.CallerClientId, caller.ClientId))
            throw ForbiddenApproval();
        if (approval.ExpiresAt <= now || approval.IssuedAt > now)
            throw new DomainException("approval_expired", "The publication approval has expired. Review the draft before trying again.");
    }

    private static bool SameClient(string left, string right) => Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) && a == b;
    private static string Partition(string id) => $"quiz-{id}";
    private static DomainException NotFound() => new("not_found", "The requested resource is unavailable.");
    private static DomainException ForbiddenApproval() => new("approval_invalid", "This approval does not authorise this publication.");
    private static DomainException InvalidRecord() => new("invalid_record", "The stored record failed integrity validation.");
    private static DomainException Busy() => new("store_busy", "The record changed concurrently. Please try again.");
}
