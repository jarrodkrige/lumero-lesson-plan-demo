using System.Net;
using System.Net.Http.Headers;
using LessonPlanner.Core;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class McpTransportTests(LocalTableFixture storage)
{
    [Fact]
    public async Task OfficialStreamableHttpClientPublishesAndRetriesAcrossServerRestart()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, draft.Owner, TestData.ClientId, CancellationToken.None);
        var caller = new PublicationCaller(TestData.Presenter, TestData.ClientId);
        PublishedQuiz receipt;
        await using (var server = await McpTestHost.StartAsync(storage))
        {
            receipt = await server.Client(storage).PublishAsync(TestData.Approved(approval), caller, CancellationToken.None);
            Assert.Equal(draft.Id, receipt.Id);
            Assert.Equal(storage.Policy.PublicUrl(draft.Id), receipt.PublicUrl);
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri(server.Endpoint, "/health/live"))).StatusCode);
        }
        await using (var restarted = await McpTestHost.StartAsync(storage))
        {
            var retry = await restarted.Client(storage).PublishAsync(TestData.Approved(approval), caller, CancellationToken.None);
            Assert.Equal(receipt, retry);
        }
        Assert.NotNull(await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Dropped_publication_response_leaves_one_durable_outcome_for_the_official_client_retry()
    {
        var draft = await storage.SaveDraftAsync();
        var approval = await storage.Store().ApproveAsync(draft.Id, draft.Owner, TestData.ClientId, CancellationToken.None);
        var caller = new PublicationCaller(draft.Owner, TestData.ClientId);
        await using var server = await McpTestHost.StartAsync(storage, dropFirstPublicationResponse: draft.Id);
        using var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await Record.ExceptionAsync(() => server.Client(storage).PublishAsync(TestData.Approved(approval), caller, firstDeadline.Token));
        Assert.True(server.ResponseWasDropped);
        Assert.NotNull(await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None));
        using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retry = await server.Client(storage).PublishAsync(TestData.Approved(approval), caller, retryDeadline.Token);
        Assert.Equal(draft.Id, retry.Id);
        Assert.Equal(storage.Policy.PublicUrl(draft.Id), retry.PublicUrl);
        var rows = new List<string>();
        await foreach (var row in storage.NewClient().QueryAsync<Azure.Data.Tables.TableEntity>($"PartitionKey eq 'quiz-{draft.Id}'"))
            rows.Add(row.RowKey);
        Assert.Equal(["approval", "draft", "publication"], rows.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("missing-role", HttpStatusCode.Forbidden)]
    [InlineData("wrong-tenant", HttpStatusCode.Forbidden)]
    [InlineData("wrong-caller", HttpStatusCode.Forbidden)]
    [InlineData("delegated", HttpStatusCode.Forbidden)]
    public async Task IndependentApiPolicyRejectsUntrustedTokens(string variation, HttpStatusCode expected)
    {
        await using var server = await McpTestHost.StartAsync(storage);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token(variation));
        using var response = await http.PostAsync(server.Endpoint, new StringContent("{}", new MediaTypeHeaderValue("application/json")));
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousAndBrowserOriginRequestsAreRejected()
    {
        await using var server = await McpTestHost.StartAsync(storage);
        using var http = new HttpClient();
        using var anonymous = await http.PostAsync(server.Endpoint, new StringContent("{}", new MediaTypeHeaderValue("application/json")));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token());
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using var browser = await http.PostAsync(server.Endpoint, new StringContent("{}", new MediaTypeHeaderValue("application/json")));
        Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
    }

    [Fact]
    public async Task ToolRejectsAlteredAndCrossPresenterApprovalWithoutPublication()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, draft.Owner, TestData.ClientId, CancellationToken.None);
        await using var server = await McpTestHost.StartAsync(storage);
        var client = server.Client(storage);
        var caller = new PublicationCaller(TestData.Presenter, TestData.ClientId);
        var altered = TestData.Approved(approval) with { PayloadHash = new string('f', 64) };
        await Assert.ThrowsAsync<DomainException>(() => client.PublishAsync(altered, caller, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => client.PublishAsync(TestData.Approved(approval),
            caller with { Presenter = caller.Presenter with { ObjectId = Guid.NewGuid() } }, CancellationToken.None));
        Assert.Null(await store.FindPublishedAsync(draft.Id, CancellationToken.None));
    }
}
