using System.Net;
using System.Text.RegularExpressions;
using LessonPlanner.Core;
using Microsoft.Extensions.DependencyInjection;

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
