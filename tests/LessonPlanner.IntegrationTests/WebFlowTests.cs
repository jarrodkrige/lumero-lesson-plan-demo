using System.Net;
using System.Text.RegularExpressions;
using LessonPlanner.Core;
using LessonPlanner.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class WebFlowTests(LocalTableFixture storage)
{
    [Fact]
    public async Task Protected_native_forms_publish_through_real_mcp_and_grade_only_on_the_server()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        var (lessonId, draftId, draftHtml) = await CreateDraftAsync(presenter);
        using var participant = host.Client();
        using var unavailable = await participant.GetAsync($"/quiz/{draftId}");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);

        var publication = HtmlForms.Fields(draftHtml, $"/presenter/drafts/{draftId}/publish");
        publication["acknowledged"] = "yes";
        presenter.DefaultRequestHeaders.Add("X-Lumero-Action", "fetch");
        using var published = await presenter.PostAsync($"/presenter/drafts/{draftId}/publish", new FormUrlEncodedContent(publication));
        Assert.Equal(HttpStatusCode.SeeOther, published.StatusCode);
        Assert.Equal($"/presenter/drafts/{draftId}", published.Headers.Location?.OriginalString);
        var counters = host.Services.GetRequiredService<LocalVerificationCounters>();
        Assert.Equal(1, counters.ConfirmedPublications);
        Assert.Equal(1, counters.TransportCalls);

        using var repeated = await presenter.PostAsync($"/presenter/drafts/{draftId}/publish", new FormUrlEncodedContent(publication));
        Assert.Equal(HttpStatusCode.SeeOther, repeated.StatusCode);
        Assert.Equal(1, counters.TransportCalls);

        using var quiz = await participant.GetAsync($"/quiz/{draftId}");
        Assert.Equal(HttpStatusCode.OK, quiz.StatusCode);
        Assert.Equal(draftId, quiz.Headers.GetValues("X-Lumero-Quiz-Id").Single());
        var quizHtml = await quiz.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private marking explanation", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CorrectOptionId", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("script-src 'none';", quiz.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("_framework/blazor", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, Regex.Matches(quizHtml, "<fieldset\\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Count);
        var answers = HtmlForms.Fields(quizHtml, $"/quiz/{draftId}/submit");
        for (var index = 1; index <= 5; index++) answers[$"answer-q{index}"] = index <= 3 ? "a" : "b";
        using var submitted = await participant.PostAsync($"/quiz/{draftId}/submit", new FormUrlEncodedContent(answers));
        Assert.Equal(HttpStatusCode.SeeOther, submitted.StatusCode);
        Assert.Equal($"/quiz/{draftId}/results/{answers["attempt"]}", submitted.Headers.Location?.OriginalString);
        using var result = await participant.GetAsync(submitted.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var resultHtml = await result.Content.ReadAsStringAsync();
        Assert.Contains("private marking explanation", resultHtml, StringComparison.OrdinalIgnoreCase);
        var stored = await storage.Store().FindResultAsync(draftId, answers["attempt"], CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.Score);
        Assert.Equal(5, stored.Total);
        using var duplicate = await participant.PostAsync($"/quiz/{draftId}/submit", new FormUrlEncodedContent(answers));
        Assert.Equal(HttpStatusCode.SeeOther, duplicate.StatusCode);
        Assert.Equal(submitted.Headers.Location, duplicate.Headers.Location);

        using var document = await presenter.GetAsync($"/presenter/lessons/{lessonId}/document");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", document.Content.Headers.ContentType?.MediaType);
        var bytes = await document.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
        using var qr = await presenter.GetAsync($"/presenter/drafts/{draftId}/qr");
        Assert.True(qr.StatusCode == HttpStatusCode.OK,
            qr.IsSuccessStatusCode ? null : await qr.Content.ReadAsStringAsync());
        Assert.Equal("image/png", qr.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, (await qr.Content.ReadAsByteArrayAsync())[..8]);
    }

    [Fact]
    public async Task Synthetic_oak_sources_are_attributed_on_lesson_quiz_and_result_pages()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var store = storage.Store();
        var lesson = TestData.LessonWithOakSources();
        var draft = TestData.DraftWithLessonSources(lesson);
        await store.SaveAsync(lesson, CancellationToken.None);
        await store.SaveDraftAsync(draft, CancellationToken.None);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(approval), new(TestData.Presenter, TestData.ClientId), CancellationToken.None);
        var attempt = TestData.Attempt(draft);
        await store.SubmitAsync(attempt, CancellationToken.None);

        using var presenter = host.Client();
        await host.SignInAsync(presenter);
        using var participant = host.Client();
        using var lessonResponse = await presenter.GetAsync($"/presenter/lessons/{lesson.Id}");
        using var quizResponse = await participant.GetAsync($"/quiz/{draft.Id}");
        using var resultResponse = await participant.GetAsync($"/quiz/{draft.Id}/results/{attempt.Id}");
        Assert.Equal(HttpStatusCode.OK, lessonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, quizResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        var lessonHtml = await lessonResponse.Content.ReadAsStringAsync();
        var quizHtml = await quizResponse.Content.ReadAsStringAsync();
        var resultHtml = await resultResponse.Content.ReadAsStringAsync();
        foreach (var html in new[] { lessonHtml, quizHtml, resultHtml })
        {
            Assert.Contains("A computing lesson by", html, StringComparison.Ordinal);
            Assert.Contains(SortingTeachingSources.OakUrl, html, StringComparison.Ordinal);
            Assert.Contains(SortingTeachingSources.LicenceUrl, html, StringComparison.Ordinal);
            foreach (var source in SortingTeachingSources.All)
                Assert.Contains(source.Url.AbsoluteUri, html, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("correctOptionId", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private marking explanation", quizHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private marking explanation", resultHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_page_retains_attribution_without_a_second_public_quiz_lookup(bool invalidRecord)
    {
        var store = storage.Store();
        var lesson = TestData.LessonWithOakSources();
        var draft = TestData.DraftWithLessonSources(lesson);
        await store.SaveAsync(lesson, CancellationToken.None);
        await store.SaveDraftAsync(draft, CancellationToken.None);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(approval), new(TestData.Presenter, TestData.ClientId), CancellationToken.None);
        var attempt = TestData.Attempt(draft);
        await store.SubmitAsync(attempt, CancellationToken.None);
        var unavailable = new UnavailablePublicQuizStore(store, invalidRecord);
        await using var host = await WebTestHost.StartAsync(storage, configureTestServices: services =>
        {
            services.RemoveAll<IQuizStore>();
            services.AddSingleton<IQuizStore>(unavailable);
        });
        using var participant = host.Client();
        using var response = await participant.GetAsync($"/quiz/{draft.Id}/results/{attempt.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, unavailable.PublicLookups);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("private marking explanation", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SortingTeachingSources.LicenceUrl, html, StringComparison.Ordinal);
        foreach (var source in SortingTeachingSources.All)
            Assert.Contains(source.Url.AbsoluteUri, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presenter_routes_require_authorisation_and_do_not_expose_another_presenters_records()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var lesson = TestData.Lesson();
        await storage.Store().SaveAsync(lesson, CancellationToken.None);
        using var anonymous = host.Client();
        foreach (var path in new[] { "/presenter", $"/presenter/lessons/{lesson.Id}", $"/presenter/lessons/{lesson.Id}/document" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("login.microsoftonline.com", response.Headers.Location?.Host);
        }
        using var other = host.Client();
        await host.SignInAsync(other, "second");
        using var privateLesson = await other.GetAsync($"/presenter/lessons/{lesson.Id}");
        using var privateDocument = await other.GetAsync($"/presenter/lessons/{lesson.Id}/document");
        Assert.Equal(HttpStatusCode.NotFound, privateLesson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, privateDocument.StatusCode);
        Assert.DoesNotContain(lesson.Lesson!.Title, await privateLesson.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var denied = host.Client();
        await host.SignInAsync(denied, "denied");
        using var deniedPage = await denied.GetAsync("/presenter");
        Assert.Equal(HttpStatusCode.Redirect, deniedPage.StatusCode);
        Assert.Equal("login.microsoftonline.com", deniedPage.Headers.Location?.Host);
    }

    [Fact]
    public async Task Mutation_endpoints_require_antiforgery_even_for_an_authenticated_presenter()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var client = host.Client();
        await host.SignInAsync(client);
        var id = DomainRules.NewId();
        foreach (var path in new[] { "/auth/sign-out", "/presenter/lessons", $"/presenter/lessons/{id}/quiz", $"/presenter/drafts/{id}/publish", $"/quiz/{id}/submit" })
        {
            using var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["acknowledged"] = "yes" }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("payloadHash", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("acknowledged", "no")]
    public async Task Altered_publication_form_cannot_create_an_approval_or_call_mcp(string field, string value)
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var client = host.Client();
        await host.SignInAsync(client);
        var (_, draftId, html) = await CreateDraftAsync(client);
        var form = HtmlForms.Fields(html, $"/presenter/drafts/{draftId}/publish");
        form["acknowledged"] = "yes";
        form[field] = value;
        using var response = await client.PostAsync($"/presenter/drafts/{draftId}/publish", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains("error=", response.Headers.Location?.OriginalString);
        Assert.Null(await storage.Store().FindPublishedAsync(draftId, CancellationToken.None));
        Assert.Equal(0, host.Services.GetRequiredService<LocalVerificationCounters>().TransportCalls);
        var approval = await storage.Table.GetEntityIfExistsAsync<Azure.Data.Tables.TableEntity>($"quiz-{draftId}", "approval");
        Assert.False(approval.HasValue);
    }

    [Fact]
    public async Task Enhanced_actions_return_confirmed_locations_and_keep_antiforgery()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var client = host.Client();
        await host.SignInAsync(client);
        client.DefaultRequestHeaders.Add("X-Lumero-Action", "fetch");
        var form = HtmlForms.Fields(await client.GetStringAsync("/presenter"), "/presenter/lessons");
        form["Topic"] = "Sorting algorithms";
        form["EducationalLevel"] = "GCSE (OCR J277)";
        form["DurationMinutes"] = "30";
        form["Instructions"] = "";
        using var generated = await client.PostAsync("/presenter/lessons", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        using var result = System.Text.Json.JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
        var lessonPath = result.RootElement.GetProperty("location").GetString()!;
        Assert.Matches("^/presenter/lessons/[a-f0-9]{48}$", lessonPath);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.RootElement.GetProperty("message").ValueKind);
        var lessonId = lessonPath.Split('/').Last();
        var quizForm = HtmlForms.Fields(await client.GetStringAsync(lessonPath), $"{lessonPath}/quiz");
        using var drafted = await client.PostAsync($"{lessonPath}/quiz", new FormUrlEncodedContent(quizForm));
        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        using var quiz = System.Text.Json.JsonDocument.Parse(await drafted.Content.ReadAsStringAsync());
        var quizPath = quiz.RootElement.GetProperty("location").GetString()!;
        Assert.Matches("^/presenter/drafts/[a-f0-9]{48}$", quizPath);
        Assert.NotNull(await storage.Store().FindDraftAsync(quizPath.Split('/').Last(), TestData.Presenter, CancellationToken.None));
        using var unverified = await client.PostAsync($"{lessonPath}/quiz", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, unverified.StatusCode);
        Assert.NotNull(await storage.Store().FindAsync(lessonId, TestData.Presenter, CancellationToken.None));
    }

    [Fact]
    public async Task Enhanced_actions_report_busy_and_validation_without_starting_work()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var client = host.Client();
        await host.SignInAsync(client);
        client.DefaultRequestHeaders.Add("X-Lumero-Action", "fetch");
        var form = HtmlForms.Fields(await client.GetStringAsync("/presenter"), "/presenter/lessons");
        form["Topic"] = "Sorting algorithms";
        form["EducationalLevel"] = "GCSE (OCR J277)";
        form["DurationMinutes"] = "30";
        using (host.Services.GetRequiredService<ModelWorkGate>().TryEnter(TestData.Presenter))
        {
            foreach (var path in new[] { "/presenter/lessons", $"/presenter/lessons/{DomainRules.NewId()}/quiz" })
            {
                using var busy = await client.PostAsync(path, new FormUrlEncodedContent(form));
                Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
                using var body = System.Text.Json.JsonDocument.Parse(await busy.Content.ReadAsStringAsync());
                Assert.Equal(System.Text.Json.JsonValueKind.Null, body.RootElement.GetProperty("location").ValueKind);
                Assert.Contains("Wait a moment", body.RootElement.GetProperty("message").GetString());
            }
        }
        form["DurationMinutes"] = "invalid";
        using var invalid = await client.PostAsync("/presenter/lessons", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Contains("Check the topic", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Services.GetRequiredService<LocalVerificationCounters>().TransportCalls);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("attempt")]
    [InlineData("answer")]
    [InlineData("extra")]
    public async Task Tampered_anonymous_form_cannot_create_an_attempt(string alteration)
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var store = storage.Store();
        var draft = await storage.SaveDraftAsync(store);
        var approval = await store.ApproveAsync(draft.Id, TestData.Presenter, TestData.ClientId, CancellationToken.None);
        await store.PublishAsync(TestData.Approved(approval), new(TestData.Presenter, TestData.ClientId), CancellationToken.None);
        using var client = host.Client();
        var html = await client.GetStringAsync($"/quiz/{draft.Id}");
        var form = HtmlForms.Fields(html, $"/quiz/{draft.Id}/submit");
        for (var index = 1; index <= 5; index++) form[$"answer-q{index}"] = "a";
        var originalAttempt = form["attempt"];
        if (alteration == "ticket") form["ticket"] = "tampered-ticket";
        if (alteration == "attempt") form["attempt"] = DomainRules.NewId();
        if (alteration == "answer") form["answer-q1"] = "unknown-option";
        if (alteration == "extra") form["teacherApproved"] = "true";
        using var response = await client.PostAsync($"/quiz/{draft.Id}/submit", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains("error=", response.Headers.Location?.OriginalString);
        Assert.Null(await store.FindResultAsync(draft.Id, originalAttempt, CancellationToken.None));
    }

    internal static async Task<(string LessonId, string DraftId, string DraftHtml)> CreateDraftAsync(HttpClient client)
    {
        var create = HtmlForms.Fields(await client.GetStringAsync("/presenter"), "/presenter/lessons");
        create["Topic"] = "Sorting algorithms";
        create["EducationalLevel"] = "GCSE (OCR J277)";
        create["DurationMinutes"] = "60";
        create["Instructions"] = "Use number cards and paired explanations.";
        using var generated = await client.PostAsync("/presenter/lessons", new FormUrlEncodedContent(create));
        Assert.Equal(HttpStatusCode.SeeOther, generated.StatusCode);
        var lessonPath = generated.Headers.Location!.OriginalString;
        Assert.DoesNotContain("error=", lessonPath, StringComparison.Ordinal);
        var lessonId = lessonPath.Split('/').Last();
        var draftForm = HtmlForms.Fields(await client.GetStringAsync(lessonPath), $"/presenter/lessons/{lessonId}/quiz");
        using var drafted = await client.PostAsync($"/presenter/lessons/{lessonId}/quiz", new FormUrlEncodedContent(draftForm));
        Assert.Equal(HttpStatusCode.SeeOther, drafted.StatusCode);
        var draftPath = drafted.Headers.Location!.OriginalString;
        Assert.DoesNotContain("error=", draftPath, StringComparison.Ordinal);
        return (lessonId, draftPath.Split('/').Last(), await client.GetStringAsync(draftPath));
    }

    private sealed class UnavailablePublicQuizStore(IQuizStore inner, bool invalidRecord) : IQuizStore
    {
        public int PublicLookups { get; private set; }
        public Task<QuizResult?> FindResultAsync(string quizId, string attemptId, CancellationToken cancellationToken) =>
            inner.FindResultAsync(quizId, attemptId, cancellationToken);
        public Task<PublicQuiz?> FindPublishedAsync(string id, CancellationToken cancellationToken)
        {
            PublicLookups++;
            if (invalidRecord) throw new DomainException("invalid_record", "The stored record failed integrity validation.");
            return Task.FromResult<PublicQuiz?>(null);
        }
        public Task SaveDraftAsync(QuizDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuizDraft?> FindDraftAsync(string id, PresenterIdentity presenter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublicationApproval> ApproveAsync(string draftId, PresenterIdentity presenter, string callerClientId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PublishedQuiz> PublishAsync(ApprovedPublication request, PublicationCaller caller, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<QuizResult> SubmitAsync(QuizAttempt attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

internal static class HtmlForms
{
    public static Dictionary<string, string> Fields(string html, string action)
    {
        var forms = Regex.Matches(html, "<form\\b(?<attributes>[^>]*)>(?<content>.*?)</form>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var form = forms.Single(match => Attribute(match.Groups["attributes"].Value, "action") == action);
        return Regex.Matches(form.Groups["content"].Value, "<input\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Where(match => Attribute(match.Value, "type") == "hidden")
            .ToDictionary(match => Attribute(match.Value, "name"), match => Attribute(match.Value, "value"), StringComparer.Ordinal);
    }

    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{Regex.Escape(name)}=\"([^\"]*)\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
