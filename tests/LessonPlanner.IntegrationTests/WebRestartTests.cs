using System.Net;
using LessonPlanner.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class WebRestartTests(LocalTableFixture storage)
{
    [Fact]
    public async Task Replacing_web_and_mcp_hosts_preserves_approval_cookies_unsubmitted_tickets_and_results()
    {
        WebTestHost? host = await WebTestHost.StartAsync(storage);
        using var presenter = host.Client(new CookieContainer());
        using var participant = host.Client(new CookieContainer());
        try
        {
            var port = host.Origin.Port;
            await host.SignInAsync(presenter);
            var (_, draftId, draftHtml) = await WebFlowTests.CreateDraftAsync(presenter);
            var publication = HtmlForms.Fields(draftHtml, $"/presenter/drafts/{draftId}/publish");
            publication["acknowledged"] = "yes";
            using var published = await presenter.PostAsync($"/presenter/drafts/{draftId}/publish", new FormUrlEncodedContent(publication));
            Assert.Equal(HttpStatusCode.SeeOther, published.StatusCode);
            var quizHtml = await participant.GetStringAsync($"/quiz/{draftId}");
            var answers = HtmlForms.Fields(quizHtml, $"/quiz/{draftId}/submit");
            for (var index = 1; index <= 5; index++) answers[$"answer-q{index}"] = "a";

            await host.DisposeAsync();
            host = null;
            host = await WebTestHost.StartAsync(storage, port);
            using var privateDraft = await presenter.GetAsync($"/presenter/drafts/{draftId}");
            Assert.Equal(HttpStatusCode.OK, privateDraft.StatusCode);
            using var submitted = await participant.PostAsync($"/quiz/{draftId}/submit", new FormUrlEncodedContent(answers));
            Assert.Equal(HttpStatusCode.SeeOther, submitted.StatusCode);
            var resultPath = $"/quiz/{draftId}/results/{answers["attempt"]}";
            Assert.Equal(resultPath, submitted.Headers.Location?.OriginalString);
            var stored = await storage.Store().FindResultAsync(draftId, answers["attempt"], CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(5, stored.Score);

            await host.DisposeAsync();
            host = null;
            host = await WebTestHost.StartAsync(storage, port);
            using var result = await participant.GetAsync(resultPath);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Contains("private marking explanation", await result.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            var afterRestart = await storage.Store().FindResultAsync(draftId, answers["attempt"], CancellationToken.None);
            Assert.Equivalent(stored, afterRestart, strict: true);
            using var repeatedPublication = await presenter.PostAsync($"/presenter/drafts/{draftId}/publish", new FormUrlEncodedContent(publication));
            Assert.Equal(HttpStatusCode.SeeOther, repeatedPublication.StatusCode);
            Assert.Equal($"/presenter/drafts/{draftId}", repeatedPublication.Headers.Location?.OriginalString);
            Assert.Equal(0, host.Services.GetRequiredService<LocalVerificationCounters>().TransportCalls);
            using var repeatedAnswers = await participant.PostAsync($"/quiz/{draftId}/submit", new FormUrlEncodedContent(answers));
            Assert.Equal(HttpStatusCode.SeeOther, repeatedAnswers.StatusCode);
            Assert.Equal(resultPath, repeatedAnswers.Headers.Location?.OriginalString);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
        }
    }
}
