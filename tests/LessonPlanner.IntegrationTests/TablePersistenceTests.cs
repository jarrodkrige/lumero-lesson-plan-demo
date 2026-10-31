using Azure;
using Azure.Data.Tables;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class TablePersistenceTests(LocalTableFixture storage)
{
    private static readonly PublicationCaller Caller = new(TestData.Presenter, TestData.ClientId);

    [Fact]
    public async Task UnpublishedDraftHasNoPublicProjectionAndCannotBeSubmitted()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        Assert.Null(await store.FindPublishedAsync(draft.Id, CancellationToken.None));
        var error = await Assert.ThrowsAsync<DomainException>(() => store.SubmitAsync(TestData.Attempt(draft), CancellationToken.None));
        Assert.Equal("not_found", error.Code);
    }

    [Fact]
    public async Task ImmutableLessonAndDraftRejectChangedContent()
    {
        var store = storage.Store();
        var lesson = TestData.Lesson();
        await store.SaveAsync(lesson, CancellationToken.None);
        await store.SaveAsync(lesson, CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(() => store.SaveAsync(lesson with { CreatedAt = lesson.CreatedAt.AddSeconds(1) }, CancellationToken.None));
        var draft = TestData.Draft(lesson);
        await store.SaveDraftAsync(draft, CancellationToken.None);
        await store.SaveDraftAsync(draft, CancellationToken.None);
        var altered = draft with { Content = draft.Content with { Title = "Changed immutable content" } };
        altered = altered with { PayloadHash = DomainRules.DraftHash(altered) };
        var error = await Assert.ThrowsAsync<DomainException>(() => store.SaveDraftAsync(altered, CancellationToken.None));
        Assert.Equal("immutable_conflict", error.Code);
    }

    [Fact]
    public async Task DraftMustBelongToAnExistingReviewedLesson()
    {
        var draft = TestData.Draft(TestData.Lesson());
        var error = await Assert.ThrowsAsync<DomainException>(() => storage.Store().SaveDraftAsync(draft, CancellationToken.None));
        Assert.Equal("draft_provenance", error.Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task ConcurrentPublicationConsumesOneApprovalAndReturnsOneReceipt(int parallelCallers)
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approvals = await Task.WhenAll(Enumerable.Range(0, parallelCallers).Select(_ =>
            storage.Store().ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None)));
        Assert.Single(approvals.Select(a => a.Id).Distinct());
        var request = TestData.Approved(approvals[0]);
        var publications = await Task.WhenAll(Enumerable.Range(0, parallelCallers).Select(_ =>
            storage.Store().PublishAsync(request, Caller, CancellationToken.None)));
        Assert.All(publications, receipt => Assert.Equal(publications[0], receipt));
        var approvalRow = await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "approval");
        Assert.True(approvalRow.Value.GetBoolean("Consumed"));
        var receiptRow = await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "publication");
        Assert.Equal(0, receiptRow.Value.GetInt32("SubmissionCount"));
        var restarted = storage.Store();
        Assert.Equal(publications[0], await restarted.PublishAsync(request, Caller, CancellationToken.None));
        Assert.NotNull(await restarted.FindPublishedAsync(draft.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MissingAlteredCrossUserAndWrongClientApprovalsAreRejected()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        var request = TestData.Approved(approval);
        var cases = new[]
        {
            (request with { ApprovalId = DomainRules.NewId() }, Caller),
            (request with { Version = 2 }, Caller),
            (request with { PayloadHash = new string('b', 64) }, Caller),
            (request, Caller with { Presenter = TestData.Presenter with { ObjectId = Guid.NewGuid() } }),
            (request, Caller with { ClientId = Guid.NewGuid().ToString("D") })
        };
        foreach (var (input, caller) in cases)
            await Assert.ThrowsAsync<DomainException>(() => store.PublishAsync(input, caller, CancellationToken.None));
        Assert.Null(await store.FindPublishedAsync(draft.Id, CancellationToken.None));
        var row = await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "approval");
        Assert.False(row.Value.GetBoolean("Consumed"));
    }

    [Fact]
    public async Task ExpiredApprovalCannotCreateOrReplayPublication()
    {
        var store = storage.Store();
        var unpublished = await storage.SaveDraftAsync(store);
        var published = await storage.SaveDraftAsync(store);
        var first = await store.ApproveAsync(unpublished.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        var second = await store.ApproveAsync(published.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(second), Caller, CancellationToken.None);
        storage.Clock.Advance(TimeSpan.FromMinutes(6));
        foreach (var approval in new[] { first, second })
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => store.PublishAsync(TestData.Approved(approval), Caller, CancellationToken.None));
            Assert.Equal("approval_expired", error.Code);
        }
        var replacement = await store.ApproveAsync(unpublished.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        Assert.NotEqual(first.Id, replacement.Id);
        await store.PublishAsync(TestData.Approved(replacement), Caller, CancellationToken.None);
    }

    [Fact]
    public async Task PublicProjectionHasNoMarkingDataAndGradingPersistsAcrossClients()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        await PublishAsync(store, draft);
        var quiz = await store.FindPublishedAsync(draft.Id, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(quiz, DomainRules.Json);
        Assert.DoesNotContain("correctOptionId", json);
        Assert.DoesNotContain("private marking explanation", json);
        var attempt = TestData.Attempt(draft);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => storage.Store().SubmitAsync(attempt, CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(5, result.Score));
        Assert.Single(results.Select(result => DomainRules.Hash(result)).Distinct());
        var restored = await storage.Store().FindResultAsync(draft.Id, attempt.Id, CancellationToken.None);
        Assert.Equal(DomainRules.Hash(results[0]), DomainRules.Hash(restored));
        var row = await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "publication");
        Assert.Equal(1, row.Value.GetInt32("SubmissionCount"));
        var changed = attempt with { Answers = TestData.Attempt(draft, "b").Answers };
        var error = await Assert.ThrowsAsync<DomainException>(() => store.SubmitAsync(changed, CancellationToken.None));
        Assert.Equal("attempt_conflict", error.Code);
    }

    [Fact]
    public async Task ConcurrentSubmissionsNeverExceedDurableBudget()
    {
        var store = storage.Store(maximumAttempts: 2);
        var draft = await storage.SaveDraftAsync(store);
        await PublishAsync(store, draft);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await storage.Store().SubmitAsync(TestData.Attempt(draft), CancellationToken.None); return true; }
            catch (DomainException e) when (e.Code is "quiz_full" or "store_busy") { return false; }
        }));
        Assert.Equal(2, outcomes.Count(success => success));
        var row = await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "publication");
        Assert.Equal(2, row.Value.GetInt32("SubmissionCount"));
    }

    [Fact]
    public async Task FailedAzureTransactionLeavesNoPartialWrite()
    {
        var partition = $"rollback-{DomainRules.NewId()}";
        var existing = new TableEntity(partition, "existing") { ["Value"] = 1 };
        await storage.Table.AddEntityAsync(existing);
        await Assert.ThrowsAsync<TableTransactionFailedException>(() => storage.Table.SubmitTransactionAsync([
            new(TableTransactionActionType.Add, new TableEntity(partition, "must-not-exist")),
            new(TableTransactionActionType.Add, new TableEntity(partition, "existing"))]));
        Assert.False((await storage.Table.GetEntityIfExistsAsync<TableEntity>(partition, "must-not-exist")).HasValue);
    }

    [Fact]
    public async Task CorruptPersistedDraftFailsClosed()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var row = (await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "draft")).Value;
        row["PayloadHash"] = new string('f', 64);
        await storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace);
        var error = await Assert.ThrowsAsync<DomainException>(() => store.FindDraftAsync(draft.Id, draft.Owner, CancellationToken.None));
        Assert.Equal("invalid_record", error.Code);
    }

    private static async Task PublishAsync(AzureTableStore store, QuizDraft draft)
    {
        var approval = await store.ApproveAsync(draft.Id, draft.Owner, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(approval), Caller, CancellationToken.None);
    }
}
