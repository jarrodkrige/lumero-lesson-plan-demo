using System.Net;
using LessonPlanner.Core;
using LessonPlanner.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class WebBoundaryFlowTests(LocalTableFixture storage)
{
    private const string SensitiveMarker = "synthetic-sensitive-test-marker";

    [Fact]
    public async Task Deep_routes_load_rooted_stylesheets_without_a_blocked_base_element()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var draft = await PublishFixtureAsync();
        using var client = host.Client();
        await host.SignInAsync(client);
        foreach (var path in new[] { $"/presenter/drafts/{draft.Id}", $"/quiz/{draft.Id}" })
        {
            using var page = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("base-uri 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            var html = await page.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<base ", html, StringComparison.OrdinalIgnoreCase);
            var stylesheets = System.Text.RegularExpressions.Regex.Matches(html, "href=\"(?<path>/[^\"]+\\.css)\"");
            Assert.Equal(2, stylesheets.Count);
            foreach (System.Text.RegularExpressions.Match stylesheet in stylesheets)
            {
                using var asset = await client.GetAsync(stylesheet.Groups["path"].Value);
                Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
                Assert.Equal("text/css", asset.Content.Headers.ContentType?.MediaType);
            }
        }
    }

    [Fact]
    public async Task Duplicate_answer_values_do_not_create_a_result()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var draft = await PublishFixtureAsync();
        using var participant = host.Client();
        var form = await AnswerFormAsync(participant, draft.Id);
        var values = form.ToList();
        values.Add(new("answer-q1", "b"));
        using var response = await participant.PostAsync($"/quiz/{draft.Id}/submit", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"/quiz/{draft.Id}?error=answers", response.Headers.Location?.OriginalString);
        Assert.Null(await storage.Store().FindResultAsync(draft.Id, form["attempt"], CancellationToken.None));
    }

    [Fact]
    public async Task Changed_answers_on_retry_cannot_replace_the_original_result()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var draft = await PublishFixtureAsync();
        using var participant = host.Client();
        var form = await AnswerFormAsync(participant, draft.Id);
        using var submitted = await participant.PostAsync($"/quiz/{draft.Id}/submit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, submitted.StatusCode);
        var before = await storage.Store().FindResultAsync(draft.Id, form["attempt"], CancellationToken.None);
        Assert.NotNull(before);
        form["answer-q1"] = "b";
        using var changed = await participant.PostAsync($"/quiz/{draft.Id}/submit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, changed.StatusCode);
        Assert.Equal($"/quiz/{draft.Id}?error=answers", changed.Headers.Location?.OriginalString);
        Assert.Equivalent(before, await storage.Store().FindResultAsync(draft.Id, form["attempt"], CancellationToken.None), strict: true);
    }

    [Fact]
    public async Task An_expired_native_form_ticket_cannot_create_a_result()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var draft = await PublishFixtureAsync();
        using var participant = host.Client();
        var form = await AnswerFormAsync(participant, draft.Id);
        storage.Clock.Advance(TimeSpan.FromMinutes(31));
        try
        {
            using var response = await participant.PostAsync($"/quiz/{draft.Id}/submit", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal($"/quiz/{draft.Id}?error=expired", response.Headers.Location?.OriginalString);
            Assert.Null(await storage.Store().FindResultAsync(draft.Id, form["attempt"], CancellationToken.None));
        }
        finally { storage.Clock.Advance(TimeSpan.FromMinutes(-31)); }
    }

    [Fact]
    public async Task Oversized_native_form_is_rejected_before_a_model_operation()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var form = LessonForm(await presenter.GetStringAsync("/presenter"));
        form["Topic"] = new string('x', 40_000);
        using var response = await presenter.PostAsync("/presenter/lessons", new FormUrlEncodedContent(form));
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"Expected a bounded client-error response, received {(int)response.StatusCode}.");
        Assert.Equal(0, host.Services.GetRequiredService<LocalVerificationCounters>().TransportCalls);
    }

    [Fact]
    public async Task Public_page_rate_limit_returns_a_bounded_retry_response()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var participant = host.Client();
        var missingQuiz = DomainRules.NewId();
        for (var index = 0; index < 600; index++)
        {
            using var allowed = await participant.GetAsync($"/quiz/{missingQuiz}");
            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
        }
        using var rejected = await participant.GetAsync($"/quiz/{missingQuiz}");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.True((await rejected.Content.ReadAsByteArrayAsync()).Length < 1024);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Model_action_rate_limit_reports_its_actual_ten_minute_window()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var form = LessonForm(await presenter.GetStringAsync("/presenter"));
        form["Topic"] = "x";
        for (var index = 0; index < 6; index++)
        {
            using var invalid = await presenter.PostAsync("/presenter/lessons", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.SeeOther, invalid.StatusCode);
            Assert.Equal("/presenter?error=validation", invalid.Headers.Location?.OriginalString);
        }
        using var rejected = await presenter.PostAsync("/presenter/lessons", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 500, 600);
        Assert.Equal(0, host.Services.GetRequiredService<LocalVerificationCounters>().TransportCalls);
    }

    [Fact]
    public async Task Generation_failure_has_no_sensitive_exception_or_assumed_success_in_the_response()
    {
        await using var host = await WebTestHost.StartAsync(storage, configureTestServices: services =>
        {
            services.RemoveAll<ILessonWorkflow>();
            services.AddSingleton<ILessonWorkflow, FailingWorkflow>();
        });
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        using var response = await presenter.PostAsync("/presenter/lessons",
            new FormUrlEncodedContent(LessonForm(await presenter.GetStringAsync("/presenter"))));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No success has been assumed", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Publication_failure_never_exposes_an_uncommitted_quiz()
    {
        await using var host = await WebTestHost.StartAsync(storage, configureTestServices: services =>
        {
            services.RemoveAll<IQuizPublisherAgent>();
            services.AddSingleton<IQuizPublisherAgent, FailingPublisher>();
        });
        var draft = await storage.SaveDraftAsync();
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var form = HtmlForms.Fields(await presenter.GetStringAsync($"/presenter/drafts/{draft.Id}"),
            $"/presenter/drafts/{draft.Id}/publish");
        form["acknowledged"] = "yes";
        using var response = await presenter.PostAsync($"/presenter/drafts/{draft.Id}/publish", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(SensitiveMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None));
        using var participant = host.Client();
        using var unpublished = await participant.GetAsync($"/quiz/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);
        Assert.DoesNotContain("CorrectOptionId", await unpublished.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("model")]
    [InlineData("receipt")]
    public async Task A_committed_publication_survives_a_missing_or_invalid_final_agent_response(string failure)
    {
        await using var host = await WebTestHost.StartAsync(storage, configureTestServices: services =>
        {
            services.RemoveAll<IQuizPublisherAgent>();
            services.AddSingleton(provider => new FailureAfterCommitPublisher(
                provider.GetRequiredService<IMcpPublicationClient>(), failure));
            services.AddSingleton<IQuizPublisherAgent>(provider => provider.GetRequiredService<FailureAfterCommitPublisher>());
        });
        var draft = await storage.SaveDraftAsync();
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var path = $"/presenter/drafts/{draft.Id}";
        var form = HtmlForms.Fields(await presenter.GetStringAsync(path), $"{path}/publish");
        form["acknowledged"] = "yes";
        using var response = await presenter.PostAsync($"{path}/publish", new FormUrlEncodedContent(form));
        var committed = await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None);
        Assert.NotNull(committed);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(path, response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(SensitiveMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var retry = await presenter.PostAsync($"{path}/publish", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, retry.StatusCode);
        Assert.Equal(path, retry.Headers.Location?.OriginalString);
        Assert.Equal(1, host.Services.GetRequiredService<FailureAfterCommitPublisher>().Calls);
        Assert.Equivalent(committed, await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None), strict: true);
        using var participant = host.Client();
        using var published = await participant.GetAsync($"/quiz/{draft.Id}");
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.DoesNotContain("CorrectOptionId", await published.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_committed_publication_keeps_its_link_when_the_canonical_url_cannot_be_verified()
    {
        using var unavailable = new HttpClient(new UnavailableHandler());
        await using var host = await WebTestHost.StartAsync(storage, configureTestServices: services =>
        {
            services.RemoveAll<QuizLinkVerifier>();
            services.AddSingleton(provider => new QuizLinkVerifier(
                provider.GetRequiredService<LessonPlanner.Infrastructure.QuizPolicy>(), unavailable));
        });
        var draft = await PublishFixtureAsync();
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var html = await presenter.GetStringAsync($"/presenter/drafts/{draft.Id}");
        Assert.Contains("Publication confirmed", html, StringComparison.Ordinal);
        Assert.Contains(storage.Policy.PublicUrl(draft.Id).AbsoluteUri, html, StringComparison.Ordinal);
        Assert.DoesNotContain($"src=\"/presenter/drafts/{draft.Id}/qr\"", html, StringComparison.Ordinal);
        using var qr = await presenter.GetAsync($"/presenter/drafts/{draft.Id}/qr");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, qr.StatusCode);
        Assert.Contains("not currently reachable", await qr.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(await storage.Store().FindPublishedAsync(draft.Id, CancellationToken.None));
    }

    private async Task<QuizDraft> PublishFixtureAsync()
    {
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(approval), new(TestData.Presenter, TestData.ClientId), CancellationToken.None);
        return draft;
    }

    private static async Task<Dictionary<string, string>> AnswerFormAsync(HttpClient participant, string quizId)
    {
        var form = HtmlForms.Fields(await participant.GetStringAsync($"/quiz/{quizId}"), $"/quiz/{quizId}/submit");
        for (var index = 1; index <= 5; index++) form[$"answer-q{index}"] = "a";
        return form;
    }

    private static Dictionary<string, string> LessonForm(string html)
    {
        var form = HtmlForms.Fields(html, "/presenter/lessons");
        form["Topic"] = "Sorting algorithms";
        form["EducationalLevel"] = "GCSE (OCR J277)";
        form["DurationMinutes"] = "60";
        form["Instructions"] = "Use a short card-sorting exercise.";
        return form;
    }

    private sealed class FailingWorkflow : ILessonWorkflow
    {
        public Task<LessonRecord> GenerateAsync(LessonRequest request, PresenterIdentity presenter,
            IProgress<ActivityEvent>? progress, CancellationToken cancellationToken) =>
            Task.FromException<LessonRecord>(new InvalidOperationException(SensitiveMarker));
        public Task<QuizDraft> DraftQuizAsync(string lessonId, PresenterIdentity presenter,
            IProgress<ActivityEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailureAfterCommitPublisher(IMcpPublicationClient transport, string failure) : IQuizPublisherAgent
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous,
            ReviewResult? feedback, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var receipt = await transport.PublishAsync(approval, caller, cancellationToken);
            return failure switch
            {
                "io" => throw new IOException(SensitiveMarker),
                "timeout" => throw new OperationCanceledException(SensitiveMarker),
                "model" => throw new DomainException("missing_model_confirmation", SensitiveMarker),
                "receipt" => receipt with { Id = DomainRules.NewId() },
                _ => throw new InvalidOperationException("Unknown test failure mode.")
            };
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class FailingPublisher : IQuizPublisherAgent
    {
        public Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous,
            ReviewResult? feedback, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller,
            CancellationToken cancellationToken) => Task.FromException<PublishedQuiz>(new IOException(SensitiveMarker));
    }
}
