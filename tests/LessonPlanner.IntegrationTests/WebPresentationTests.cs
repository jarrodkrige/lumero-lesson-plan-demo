using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using LessonPlanner.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LessonPlanner.IntegrationTests;

[Collection(LocalStorageCollection.Name)]
public sealed class WebPresentationTests(LocalTableFixture storage)
{
    [Fact]
    public async Task Concise_workspace_preserves_accessible_fields_post_contract_and_generic_brand()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        using var client = host.Client();
        var home = await client.GetStringAsync("/");
        PresentationHtml.GenericBrand(home);
        Assert.Contains(PresentationHtml.Elements(home, "a"), link =>
            PresentationHtml.Text(link.Groups["content"].Value) == "Sign in" && PresentationHtml.Attribute(link, "href") == "/auth/sign-in");
        await host.SignInAsync(client);
        var workspace = await client.GetStringAsync("/presenter?example=sorting");
        PresentationHtml.GenericBrand(workspace);
        Assert.Contains(PresentationHtml.Elements(workspace, "a"), link =>
            PresentationHtml.Text(link.Groups["content"].Value) == "Sorting example" && PresentationHtml.Attribute(link, "href") == "/presenter?example=sorting");
        var form = Assert.Single(PresentationHtml.Elements(workspace, "form"), candidate =>
            PresentationHtml.Attribute(candidate, "action") == "/presenter/lessons");
        Assert.Equal("post", PresentationHtml.Attribute(form, "method"));
        Assert.Equal("Lesson request", PresentationHtml.Attribute(form, "aria-label"));
        Assert.Equal("Create lesson", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(form.Value, "button")).Groups["content"].Value));
        foreach (var (id, label) in new[] { ("topic", "Topic"), ("educational-level", "Educational level"),
                     ("duration", "Duration in minutes"), ("instructions", "Teaching instructions (optional)") })
            Assert.Contains(PresentationHtml.Elements(form.Value, "label"), candidate =>
                PresentationHtml.Attribute(candidate, "for") == id && PresentationHtml.Text(candidate.Groups["content"].Value) == label);
        var fields = HtmlForms.Fields(workspace, "/presenter/lessons");
        Assert.False(string.IsNullOrWhiteSpace(fields["__RequestVerificationToken"]));
        Assert.Contains("name=\"Topic\"", form.Value, StringComparison.Ordinal);
        Assert.Contains("name=\"EducationalLevel\"", form.Value, StringComparison.Ordinal);
        Assert.Contains("name=\"DurationMinutes\"", form.Value, StringComparison.Ordinal);
        Assert.Contains("name=\"Instructions\"", form.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_lesson_keeps_full_teaching_content_and_document_provenance_in_native_disclosures()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var record = TestData.Lesson();
        var plan = record.Lesson!;
        await storage.Store().SaveAsync(record, CancellationToken.None);
        using var client = host.Client();
        await host.SignInAsync(client);
        var path = $"/presenter/lessons/{record.Id}";
        var html = await client.GetStringAsync(path);
        PresentationHtml.GenericBrand(html);
        var decoded = WebUtility.HtmlDecode(html);
        foreach (var text in plan.Objectives.Select(objective => objective.Description).Append(plan.Overview).Append(plan.Title))
            Assert.Contains(text, decoded, StringComparison.Ordinal);
        var teaching = PresentationHtml.Element(html, "details", "teaching-notes");
        Assert.False(PresentationHtml.HasAttribute(teaching, "open"));
        Assert.Equal("Teaching notes", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(teaching.Value, "summary")).Groups["content"].Value));
        foreach (var text in plan.Prerequisites.Concat(plan.Materials).Concat(plan.AssessmentQuestions).Append(plan.Explanation))
            Assert.Contains(text, WebUtility.HtmlDecode(teaching.Value), StringComparison.Ordinal);
        var activities = PresentationHtml.Elements(html, "details").Where(details => PresentationHtml.HasClass(details, "activity-detail")).ToArray();
        Assert.Equal(plan.Activities.Length, activities.Length);
        for (var index = 0; index < activities.Length; index++)
        {
            Assert.False(PresentationHtml.HasAttribute(activities[index], "open"));
            Assert.Equal(plan.Activities[index].Title, PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(activities[index].Value, "summary")).Groups["content"].Value));
            Assert.Contains(plan.Activities[index].Instructions, WebUtility.HtmlDecode(activities[index].Value), StringComparison.Ordinal);
            Assert.Contains(plan.Activities[index].Assessment, WebUtility.HtmlDecode(activities[index].Value), StringComparison.Ordinal);
        }
        var sources = PresentationHtml.Element(html, "details", "sources");
        Assert.False(PresentationHtml.HasAttribute(sources, "open"));
        foreach (var source in plan.Sources)
        {
            Assert.Contains(source.Title, WebUtility.HtmlDecode(sources.Value), StringComparison.Ordinal);
            Assert.Contains(source.Url.AbsoluteUri, WebUtility.HtmlDecode(sources.Value), StringComparison.Ordinal);
            Assert.Contains(source.Extract, WebUtility.HtmlDecode(sources.Value), StringComparison.Ordinal);
            Assert.Contains($"href=\"#source-{source.Id}\"", html, StringComparison.Ordinal);
        }
        var quizForm = Assert.Single(PresentationHtml.Elements(html, "form"), form => PresentationHtml.Attribute(form, "action") == path + "/quiz");
        Assert.Equal("post", PresentationHtml.Attribute(quizForm, "method"));
        Assert.Equal("Create quiz", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(quizForm.Value, "button")).Groups["content"].Value));
        Assert.False(string.IsNullOrWhiteSpace(HtmlForms.Fields(html, path + "/quiz")["__RequestVerificationToken"]));
        Assert.Contains(PresentationHtml.Elements(html, "a"), link =>
            PresentationHtml.Attribute(link, "href") == path + "/document" && PresentationHtml.Text(link.Groups["content"].Value) == "Download lesson");
        Assert.DoesNotContain("agent-strip", html, StringComparison.Ordinal);

        using var document = await client.GetAsync(path + "/document");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        using var archive = new ZipArchive(await document.Content.ReadAsStreamAsync());
        using var relationshipStream = archive.GetEntry("_rels/.rels")!.Open();
        var relationships = XDocument.Load(relationshipStream);
        var coreTarget = Assert.Single(relationships.Root!.Elements(), element =>
            ((string?)element.Attribute("Type"))?.EndsWith("/metadata/core-properties", StringComparison.Ordinal) == true).Attribute("Target")!.Value.TrimStart('/');
        using var coreStream = archive.GetEntry(coreTarget)!.Open();
        var core = XDocument.Load(coreStream);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        Assert.Equal("Agent demo", core.Root!.Element(dc + "creator")!.Value);
        using var bodyStream = archive.GetEntry("word/document.xml")!.Open();
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = string.Join(" ", XDocument.Load(bodyStream).Descendants(word + "t").Select(text => text.Value));
        foreach (var text in plan.Prerequisites.Concat(plan.Materials).Concat(plan.AssessmentQuestions).Append(plan.Explanation)
                     .Concat(plan.Activities.SelectMany(activity => new[] { activity.Title, activity.Instructions, activity.Assessment }))
                     .Concat(plan.Sources.SelectMany(source => new[] { source.Title, source.Url.AbsoluteUri, source.Extract, source.Sha256 })))
            Assert.Contains(text, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Lumero", body + core.Root.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RunStatus.Rejected, "Review not passed")]
    [InlineData(RunStatus.Failed, "Lesson incomplete")]
    public async Task Nonready_lessons_keep_warnings_and_review_findings_open_without_quiz_or_download_actions(RunStatus status, string statusLabel)
    {
        await using var host = await WebTestHost.StartAsync(storage);
        const string finding = "Synthetic critical finding: the full explanation must remain apparent.";
        var record = TestData.Lesson() with { Status = status, Review = new(false, [new("synthetic-review", ReviewSeverity.Critical, finding)]) };
        await storage.Store().SaveAsync(record, CancellationToken.None);
        using var client = host.Client();
        await host.SignInAsync(client);
        var path = $"/presenter/lessons/{record.Id}";
        var html = await client.GetStringAsync(path);
        PresentationHtml.GenericBrand(html);
        Assert.Contains(statusLabel, PresentationHtml.Text(html), StringComparison.Ordinal);
        Assert.Contains("This lesson is not ready for a quiz", PresentationHtml.Text(html), StringComparison.Ordinal);
        Assert.Contains("No quiz can be created from this lesson.", PresentationHtml.Text(html), StringComparison.Ordinal);
        Assert.DoesNotContain($"action=\"{path}/quiz\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"{path}/document\"", html, StringComparison.Ordinal);
        var review = PresentationHtml.Element(html, "details", "review-summary");
        Assert.True(PresentationHtml.HasAttribute(review, "open"));
        Assert.Contains(finding, PresentationHtml.Text(review.Value), StringComparison.Ordinal);
        Assert.Contains("Needs attention", PresentationHtml.Text(review.Value), StringComparison.Ordinal);
        using var document = await client.GetAsync(path + "/document");
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        var workspace = await client.GetStringAsync("/presenter");
        var fields = HtmlForms.Fields(workspace, "/presenter/lessons");
        using var forgedQuiz = await client.PostAsync(path + "/quiz", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["__RequestVerificationToken"] = fields["__RequestVerificationToken"]
        }));
        Assert.Equal(HttpStatusCode.SeeOther, forgedQuiz.StatusCode);
        Assert.Equal(path + "?error=review", forgedQuiz.Headers.Location?.OriginalString);
        var counters = host.Services.GetRequiredService<LocalVerificationCounters>();
        Assert.Equal(0, counters.TransportCalls);
        Assert.Equal(0, counters.ConfirmedPublications);
    }

    [Fact]
    public async Task Private_draft_retains_every_answer_and_approval_binding_without_fabricated_agent_history()
    {
        await using var host = await WebTestHost.StartAsync(storage);
        var draft = await storage.SaveDraftAsync();
        using var client = host.Client();
        await host.SignInAsync(client);
        var path = $"/presenter/drafts/{draft.Id}";
        var html = await client.GetStringAsync(path);
        PresentationHtml.GenericBrand(html);
        Assert.DoesNotContain("agent-strip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("activity-history", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent activity", PresentationHtml.Text(html), StringComparison.Ordinal);
        var publisher = PresentationHtml.Element(html, "p", "publisher-label");
        Assert.Equal("Quiz Publisher", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(publisher.Value, "strong")).Groups["content"].Value));
        Assert.Equal("MCP action", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(publisher.Value, "span")).Groups["content"].Value));
        foreach (var question in draft.Content.Questions)
        {
            Assert.Contains(question.Prompt, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Contains(question.Explanation, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            foreach (var option in question.Options) Assert.Contains(option.Text, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        }
        Assert.Equal(5, PresentationHtml.Elements(html, "strong").Count(element => PresentationHtml.HasClass(element, "correct-label")));
        var approval = Assert.Single(PresentationHtml.Elements(html, "form"), form => PresentationHtml.Attribute(form, "action") == path + "/publish");
        Assert.Equal("post", PresentationHtml.Attribute(approval, "method"));
        Assert.Equal("Approve quiz publication", PresentationHtml.Attribute(approval, "aria-label"));
        Assert.Equal("Publish quiz", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(approval.Value, "button")).Groups["content"].Value));
        Assert.Contains(PresentationHtml.Elements(approval.Value, "label"), label => PresentationHtml.Attribute(label, "for") == "acknowledged" &&
            PresentationHtml.Text(label.Groups["content"].Value) == "I approve this quiz for publication.");
        var fields = HtmlForms.Fields(html, path + "/publish");
        Assert.Equal(new[] { "__RequestVerificationToken", "payloadHash", "version" }, fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(draft.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), fields["version"]);
        Assert.Equal(draft.PayloadHash, fields["payloadHash"]);
        Assert.False(string.IsNullOrWhiteSpace(fields["__RequestVerificationToken"]));
        using var anonymous = host.Client();
        using var unavailable = await anonymous.GetAsync($"/quiz/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.DoesNotContain(draft.Content.Questions[0].Explanation, await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
