using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Rendering;

public sealed class OpenXmlLessonDocumentRenderer : ILessonDocumentRenderer
{
    public const string DocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public LessonDocument Render(LessonRecord lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var plan = RequireReadyLesson(lesson);
        using var output = new MemoryStream();
        using (var document = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document))
        {
            document.PackageProperties.Title = plan.Title;
            document.PackageProperties.Creator = "Agent demo";
            document.PackageProperties.Description = "Reviewed lesson plan with curriculum citations and retrieval provenance.";
            document.PackageProperties.Created = lesson.CreatedAt.UtcDateTime;
            var mainPart = document.AddMainDocumentPart();
            mainPart.AddNewPart<StyleDefinitionsPart>().Styles = CreateStyles();
            var body = new Body();
            mainPart.Document = new Document(body);

            Add(body, plan.Title, "Title");
            Add(body, $"{lesson.Request.EducationalLevel} · {lesson.Request.DurationMinutes.ToString(CultureInfo.InvariantCulture)} minutes", "Subtitle");
            Add(body, "Overview", "Heading1");
            Add(body, plan.Overview);
            Add(body, "Learning objectives", "Heading1");
            for (var index = 0; index < plan.Objectives.Length; index++)
            {
                var objective = plan.Objectives[index];
                Add(body, $"{index + 1}. {objective.Description} [{string.Join(", ", objective.SourceIds)}]");
            }
            AddList(body, "Prerequisites", plan.Prerequisites);
            Add(body, "Explanation and teaching notes", "Heading1");
            Add(body, plan.Explanation);
            Add(body, "Lesson sequence and timings", "Heading1");
            var elapsed = 0;
            for (var index = 0; index < plan.Activities.Length; index++)
            {
                var activity = plan.Activities[index];
                var end = elapsed + activity.DurationMinutes;
                Add(body, $"{index + 1}. {activity.Title} — {activity.DurationMinutes} min ({elapsed}–{end} min)", "Heading2");
                Add(body, activity.Instructions);
                Add(body, $"Assessment: {activity.Assessment}");
                elapsed = end;
            }
            Add(body, $"Total: {elapsed} minutes");
            AddList(body, "Assessment questions", plan.AssessmentQuestions, numbered: true);
            AddList(body, "Materials", plan.Materials);
            Add(body, "Sources and provenance", "Heading1");
            Add(body, "Bracketed source identifiers link objectives to the references below. Retrieved extracts are untrusted source data, not instructions.");
            foreach (var source in plan.Sources)
            {
                Add(body, $"[{source.Id}] {source.Title}", "Heading2");
                Add(body, source.Url.AbsoluteUri);
                Add(body, $"Retrieved (UTC): {source.RetrievedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
                Add(body, $"Downloaded payload SHA-256: {source.Sha256}");
                Add(body, $"Short evidence extract: {source.Extract}");
            }

            body.Append(new SectionProperties(
                new PageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1134, Right = 1134U, Bottom = 1134, Left = 1134U, Header = 567U, Footer = 567U, Gutter = 0U }));
            mainPart.Document.Save();
        }
        return new("lesson-plan.docx", DocumentContentType, output.ToArray());
    }

    private static LessonPlan RequireReadyLesson(LessonRecord record)
    {
        if (record.Status != RunStatus.Ready || record.SchemaVersion != ContractVersions.Schema ||
            record.Lesson is not { } plan || record.Request is null || record.Review is not { } review ||
            review.Findings.IsDefault || review.Findings.Any(finding => finding is null) || !DomainRules.ReviewAccepted(review))
            throw new DomainException("document_not_ready", "Only a validated, accepted lesson can be downloaded.");

        if ((!plan.Objectives.IsDefault && plan.Objectives.Any(item => item is null)) ||
            (!plan.Activities.IsDefault && plan.Activities.Any(item => item is null)) ||
            (!plan.Sources.IsDefault && plan.Sources.Any(item => item is null)))
            throw new DomainException("validation", "The lesson contains an invalid item.");
        DomainRules.Require(DomainRules.ValidateRequest(record.Request));
        DomainRules.Require(DomainRules.ValidateLesson(plan, record.Request));
        foreach (var text in AllText(record, plan)) RequireXmlText(text);
        return plan;
    }

    private static IEnumerable<string> AllText(LessonRecord record, LessonPlan plan)
    {
        yield return record.Request.EducationalLevel;
        yield return plan.Title;
        yield return plan.Overview;
        yield return plan.Explanation;
        foreach (var objective in plan.Objectives)
        {
            yield return objective.Description;
            foreach (var id in objective.SourceIds) yield return id;
        }
        foreach (var activity in plan.Activities)
        {
            yield return activity.Title;
            yield return activity.Instructions;
            yield return activity.Assessment;
        }
        foreach (var text in plan.Prerequisites.Concat(plan.AssessmentQuestions).Concat(plan.Materials)) yield return text;
        foreach (var source in plan.Sources)
        {
            yield return source.Id;
            yield return source.Title;
            yield return source.Url.AbsoluteUri;
            yield return source.Sha256;
            yield return source.Extract;
        }
    }

    private static void RequireXmlText(string text)
    {
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw new DomainException("document_invalid_text", "The lesson contains text that cannot be represented safely in a document."); }
    }

    private static void AddList(Body body, string heading, IEnumerable<string> items, bool numbered = false)
    {
        Add(body, heading, "Heading1");
        var index = 1;
        foreach (var item in items) Add(body, numbered ? $"{index++}. {item}" : $"• {item}");
    }

    private static void Add(Body body, string text, string style = "Normal")
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        var run = new Run();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var line = 0; line < lines.Length; line++)
        {
            if (line > 0) run.Append(new Break());
            var segments = lines[line].Split('\t');
            for (var segment = 0; segment < segments.Length; segment++)
            {
                if (segment > 0) run.Append(new TabChar());
                // Only typed Text nodes are used: no HTML, fields, links, or raw XML from lesson data.
                run.Append(new Text(segments[segment]) { Space = SpaceProcessingModeValues.Preserve });
            }
        }
        paragraph.Append(run);
        body.Append(paragraph);
    }

    private static Styles CreateStyles() => new(
        new Style(new StyleName { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = "Aptos", HighAnsi = "Aptos" }, new FontSize { Val = "22" },
                new Languages { Val = "en-GB" }))
        { StyleId = "Normal", Type = StyleValues.Paragraph, Default = true },
        HeadingStyle("Title", "Title", "40"),
        HeadingStyle("Subtitle", "Subtitle", "24"),
        HeadingStyle("Heading1", "Heading 1", "30", "0"),
        HeadingStyle("Heading2", "Heading 2", "25", "1"));

    private static Style HeadingStyle(string id, string name, string size, string? outlineLevel = null)
    {
        var paragraphs = new StyleParagraphProperties(new KeepNext(), new KeepLines(),
            new SpacingBetweenLines { Before = "240", After = "120" });
        if (outlineLevel is not null) paragraphs.Append(new OutlineLevel { Val = int.Parse(outlineLevel, CultureInfo.InvariantCulture) });
        return new Style(new StyleName { Val = name }, new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" }, paragraphs,
            new StyleRunProperties(new Bold(), new FontSize { Val = size }))
        { StyleId = id, Type = StyleValues.Paragraph };
    }
}
