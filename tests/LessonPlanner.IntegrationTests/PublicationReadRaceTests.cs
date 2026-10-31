using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class PublicationReadRaceTests(LocalTableFixture storage)
{
    private static readonly PublicationCaller Caller = new(TestData.Presenter, TestData.ClientId);

    [Fact]
    public async Task Publication_committed_between_independent_reads_returns_the_existing_receipt()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        var request = TestData.Approved(approval);
        PublishedQuiz? committed = null;
        var policy = new BeforePublicationRead(async () =>
            committed = await store.PublishAsync(request, Caller, CancellationToken.None));
        var receipt = await InstrumentedStore(policy).PublishAsync(request, Caller, CancellationToken.None);
        Assert.True(policy.Triggered);
        Assert.Equal(committed, receipt);
        Assert.True((await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "approval")).Value.GetBoolean("Consumed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_expiring_between_reads_cannot_commit_or_replay_a_publication(bool alreadyPublished)
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        if (alreadyPublished)
            await store.PublishAsync(TestData.Approved(approval), Caller, CancellationToken.None);
        var policy = new BeforePublicationRead(() =>
        {
            storage.Clock.Advance(TimeSpan.FromMinutes(6));
            return Task.CompletedTask;
        });
        try
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => InstrumentedStore(policy)
                .PublishAsync(TestData.Approved(approval), Caller, CancellationToken.None));
            Assert.True(policy.Triggered);
            Assert.Equal("approval_expired", error.Code);
            var publication = await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None);
            Assert.Equal(alreadyPublished, publication is not null);
            Assert.Equal(alreadyPublished,
                (await storage.Table.GetEntityAsync<TableEntity>($"quiz-{draft.Id}", "approval")).Value.GetBoolean("Consumed"));
        }
        finally
        {
            if (policy.Triggered) storage.Clock.Advance(TimeSpan.FromMinutes(-6));
        }
    }

    private AzureTableStore InstrumentedStore(HttpPipelinePolicy policy)
    {
        var options = new TableClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        };
        options.AddPolicy(policy, HttpPipelinePosition.PerCall);
        return new(new TableClient(LocalTableFixture.Connection, storage.TableName, options), storage.Policy,
            new ExactMatchGradingPolicy(), storage.Clock);
    }

    private sealed class BeforePublicationRead(Func<Task> beforeRead) : HttpPipelinePolicy
    {
        private int triggered;
        public bool Triggered => Volatile.Read(ref triggered) != 0;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            ProcessNext(message, pipeline);

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (message.Request.Method == RequestMethod.Get &&
                Uri.UnescapeDataString(message.Request.Uri.ToUri().AbsolutePath)
                    .Contains("RowKey='publication'", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref triggered, 1) == 0)
                await beforeRead();
            await ProcessNextAsync(message, pipeline);
        }
    }
}
