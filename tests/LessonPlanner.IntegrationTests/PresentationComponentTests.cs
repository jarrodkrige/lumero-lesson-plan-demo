using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using LessonPlanner.Core;
using LessonPlanner.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LessonPlanner.IntegrationTests;

public sealed class PresentationComponentTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentRole.Orchestrator, "Orchestrator", "Agent")]
    [InlineData(AgentRole.Curriculum, "Curriculum", "Agent")]
    [InlineData(AgentRole.LessonWriter, "Writer", "Agent")]
    [InlineData(AgentRole.Reviewer, "Reviewer", "Agent")]
    [InlineData(AgentRole.QuizPublisher, "Quiz Publisher", "Agent")]
    [InlineData(AgentRole.Validation, "Checks", "Application")]
    [InlineData(AgentRole.Persistence, "Save", "Application")]
    public async Task A_recorded_role_has_one_genuinely_labelled_stage(AgentRole role, string label, string kind)
    {
        var html = await RenderAsync<ActivityTimeline>(nameof(ActivityTimeline.Activities),
            new[] { Event(role, ActivityState.AwaitingApproval, 0) });
        var strip = PresentationHtml.Element(html, "ol", "agent-strip");
        var stage = Assert.Single(PresentationHtml.Elements(strip.Groups["content"].Value, "li"));
        Assert.Equal(label, PresentationHtml.Text(PresentationHtml.Element(stage.Value, "strong", "stage-name").Groups["content"].Value));
        Assert.Equal(kind, PresentationHtml.Text(PresentationHtml.Element(stage.Value, "span", "stage-kind").Groups["content"].Value));
        Assert.Equal("Awaiting approval", PresentationHtml.Text(PresentationHtml.Element(stage.Value, "span", "stage-status").Groups["content"].Value));
        Assert.Equal(kind == "Application", PresentationHtml.HasClass(stage, "application-step"));
    }

    [Fact]
    public async Task Strip_uses_latest_recorded_states_while_history_retains_every_chronological_event()
    {
        ActivityEvent[] events = [
            Event(AgentRole.Persistence, ActivityState.Failed, 8),
            Event(AgentRole.LessonWriter, ActivityState.Started, 1),
            Event(AgentRole.Orchestrator, ActivityState.Succeeded, 3),
            Event(AgentRole.LessonWriter, ActivityState.Failed, 5),
            Event(AgentRole.Validation, ActivityState.Succeeded, 6),
            Event(AgentRole.Orchestrator, ActivityState.Started, 0),
            Event(AgentRole.LessonWriter, ActivityState.RevisionRequested, 4)
        ];
        var html = await RenderAsync<ActivityTimeline>(nameof(ActivityTimeline.Activities), events);
        Assert.Contains("<h2 id=\"activity-heading\">Agent activity</h2>", html, StringComparison.Ordinal);
        var strip = PresentationHtml.Element(html, "ol", "agent-strip");
        var stages = PresentationHtml.Elements(strip.Groups["content"].Value, "li").ToArray();
        Assert.Equal(4, stages.Length);
        Assert.Equal(new[] { "Orchestrator", "Writer", "Checks", "Save" }, stages.Select(stage =>
            PresentationHtml.Text(PresentationHtml.Element(stage.Value, "strong", "stage-name").Groups["content"].Value)));
        Assert.Equal(new[] { "Completed", "Failed", "Completed", "Failed" }, stages.Select(stage =>
            PresentationHtml.Text(PresentationHtml.Element(stage.Value, "span", "stage-status").Groups["content"].Value)));
        Assert.DoesNotContain("Curriculum", strip.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Reviewer", strip.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Quiz Publisher", strip.Value, StringComparison.Ordinal);
        var history = PresentationHtml.Element(html, "details", "activity-history");
        Assert.False(PresentationHtml.HasAttribute(history, "open"));
        Assert.Equal("Recorded events", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(history.Value, "summary")).Groups["content"].Value));
        var recorded = PresentationHtml.Elements(history.Groups["content"].Value, "li").ToArray();
        (string Role, string State, int Minute)[] expected = [
            ("Orchestrator", "Started", 0), ("Writer", "Started", 1), ("Orchestrator", "Completed", 3),
            ("Writer", "Revision requested", 4), ("Writer", "Failed", 5), ("Checks", "Completed", 6), ("Save", "Failed", 8)
        ];
        Assert.Equal(expected.Length, recorded.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Role, PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(recorded[index].Value, "strong")).Groups["content"].Value));
            Assert.Equal(expected[index].State, PresentationHtml.Text(PresentationHtml.Element(recorded[index].Value, "span", "activity-state").Groups["content"].Value));
            var time = Assert.Single(PresentationHtml.Elements(recorded[index].Value, "time"));
            Assert.Equal(RecordedAt.AddMinutes(expected[index].Minute), DateTimeOffset.Parse(PresentationHtml.Attribute(time, "datetime"), CultureInfo.InvariantCulture));
            Assert.Contains("UTC", PresentationHtml.Text(time.Groups["content"].Value), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Missing_history_never_fabricates_agent_stages_or_a_recorded_events_disclosure()
    {
        var html = await RenderAsync<ActivityTimeline>(nameof(ActivityTimeline.Activities), Array.Empty<ActivityEvent>());
        Assert.Contains("No recorded events.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-strip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, ReviewSeverity.Information, false, "Review note")]
    [InlineData(false, ReviewSeverity.Revision, true, "Revision needed")]
    [InlineData(false, ReviewSeverity.Critical, true, "Needs attention")]
    [InlineData(true, ReviewSeverity.Critical, true, "Needs attention")]
    public async Task Review_disclosure_preserves_complete_findings_and_exposes_rejections_or_critical_findings(
        bool accepted, ReviewSeverity severity, bool open, string severityLabel)
    {
        const string finding = "Synthetic reviewer finding: preserve <example> & the entire explanation.\nDo not omit this final sentence.";
        var html = await RenderAsync<ReviewSummary>(nameof(ReviewSummary.Review),
            new ReviewResult(accepted, [new("synthetic-presentation", severity, finding)]));
        var details = PresentationHtml.Element(html, "details", "review-summary");
        Assert.Equal(open, PresentationHtml.HasAttribute(details, "open"));
        Assert.Equal("Review details", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(details.Value, "summary")).Groups["content"].Value));
        Assert.Contains(finding, WebUtility.HtmlDecode(details.Value), StringComparison.Ordinal);
        Assert.Contains(severityLabel, PresentationHtml.Text(details.Value), StringComparison.Ordinal);
        Assert.Contains(accepted ? "Review passed" : "Changes needed", PresentationHtml.Text(details.Value), StringComparison.Ordinal);
        Assert.DoesNotContain("<example>", details.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_sources_retain_complete_excerpts_urls_times_and_accessible_fragment_titles()
    {
        var sources = new[] {
            TestData.Sources[0] with { Extract = "Synthetic full excerpt <example> & " + new string('x', 600) + " retained ending." },
            TestData.Sources[0] with { Id = "second-source", Title = "Second synthetic reference", Url = new Uri("https://www.ocr.org.uk/example?a=1&b=2") }
        };
        var html = await RenderAsync<SourceCitations>(nameof(SourceCitations.Sources), sources);
        var details = PresentationHtml.Element(html, "details", "sources");
        Assert.False(PresentationHtml.HasAttribute(details, "open"));
        Assert.Equal("Sources (2)", PresentationHtml.Text(Assert.Single(PresentationHtml.Elements(details.Value, "summary")).Groups["content"].Value));
        foreach (var source in sources)
        {
            Assert.Contains($"id=\"source-{source.Id}\"", html, StringComparison.Ordinal);
            Assert.Contains(source.Url.AbsoluteUri, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Contains(source.Title, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Contains(source.Extract, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
            Assert.Contains(source.RetrievedAt.ToString("O", CultureInfo.InvariantCulture), WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("<example>", html, StringComparison.Ordinal);
        var links = await RenderAsync<SourceLinks>(new Dictionary<string, object?> {
            [nameof(SourceLinks.Sources)] = sources,
            [nameof(SourceLinks.SourceIds)] = new[] { "ocr-j277", "second-source", "ocr-j277" }
        });
        var anchors = PresentationHtml.Elements(links, "a").ToArray();
        Assert.Equal(2, anchors.Length);
        for (var index = 0; index < sources.Length; index++)
        {
            Assert.Equal($"Source {index + 1}", PresentationHtml.Text(anchors[index].Groups["content"].Value));
            Assert.Equal($"Source {index + 1}: {sources[index].Title}", PresentationHtml.Attribute(anchors[index], "aria-label"));
            Assert.Equal($"#source-{sources[index].Id}", PresentationHtml.Attribute(anchors[index], "href"));
        }
    }

    [Fact]
    public async Task Oak_attribution_is_visible_outside_the_source_disclosure_and_uses_trusted_links()
    {
        var lesson = TestData.LessonWithOakSources();
        var html = await RenderAsync<SourceCitations>(nameof(SourceCitations.Sources), lesson.Lesson!.Sources);
        var attribution = PresentationHtml.Element(html, "section", "after-paper");
        Assert.Equal("Source attribution", PresentationHtml.Attribute(attribution, "aria-label"));
        Assert.True(html.IndexOf(attribution.Value, StringComparison.Ordinal) >
            html.IndexOf("</details>", StringComparison.Ordinal));
        Assert.Contains("A computing lesson by Oak National Academy", PresentationHtml.Text(attribution.Value), StringComparison.Ordinal);
        var links = PresentationHtml.Elements(attribution.Value, "a").ToArray();
        Assert.Equal(5, links.Length);
        Assert.Equal(SortingTeachingSources.OakUrl, PresentationHtml.Attribute(links[0], "href"));
        Assert.Equal(SortingTeachingSources.LicenceUrl, PresentationHtml.Attribute(links[1], "href"));
        foreach (var source in SortingTeachingSources.All)
            Assert.Contains(links, link => PresentationHtml.Attribute(link, "href") == source.Url.AbsoluteUri);
    }

    private static ActivityEvent Event(AgentRole role, ActivityState state, int minute) =>
        new("synthetic-record", RecordedAt.AddMinutes(minute), role, state, "Synthetic recorded event for presentation verification.");

    private static Task<string> RenderAsync<T>(string name, object value) where T : IComponent =>
        RenderAsync<T>(new Dictionary<string, object?> { [name] = value });

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> values) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(values))).ToHtmlString());
    }
}

internal static class PresentationHtml
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    internal static IEnumerable<Match> Elements(string html, string tag) =>
        Regex.Matches(html, $"<{Regex.Escape(tag)}\\b(?<attributes>[^>]*)>(?<content>.*?)</{Regex.Escape(tag)}>", Options, Timeout).Cast<Match>();

    internal static Match Element(string html, string tag, string className) =>
        Assert.Single(Elements(html, tag), element => HasClass(element, className));

    internal static bool HasClass(Match element, string className) =>
        Attribute(element, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(className, StringComparer.Ordinal);

    internal static string Attribute(Match element, string name) => WebUtility.HtmlDecode(
        Regex.Match(element.Groups["attributes"].Value, $"(?:^|\\s){Regex.Escape(name)}=\"([^\"]*)\"", Options, Timeout).Groups[1].Value);

    internal static bool HasAttribute(Match element, string name) =>
        Regex.IsMatch(element.Groups["attributes"].Value, $"(?:^|\\s){Regex.Escape(name)}(?:\\s|=|$)", Options, Timeout);

    internal static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "", Options, Timeout)).Trim();

    internal static void GenericBrand(string html)
    {
        Assert.Contains("Agent demo", Text(Assert.Single(Elements(html, "title")).Groups["content"].Value), StringComparison.Ordinal);
        Assert.DoesNotContain("Lumero", Text(html), StringComparison.OrdinalIgnoreCase);
        foreach (Match attribute in Regex.Matches(html, "(?:aria-label|alt|title)=\"([^\"]*)\"", Options, Timeout))
            Assert.DoesNotContain("Lumero", WebUtility.HtmlDecode(attribute.Groups[1].Value), StringComparison.OrdinalIgnoreCase);
    }
}
