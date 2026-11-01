using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

internal static class OakLessonTextExtractor
{
    private const string FlightPrefix = "self.__next_f.push(";
    private static readonly string[] MergeProcedure =
    [
        "Three, repeat steps A to B until one of the lists is empty.",
        "A, compare the first items of the two lists.",
        "B, remove the items that is lower and place it into the merged list in the next available position.",
        "Four, then place each item from the remaining list into the merged list in order."
    ];

    internal static async Task<string> ExtractAsync(byte[] payload, CatalogueEntry entry, CancellationToken cancellationToken)
    {
        string html;
        try { html = new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException)
        {
            throw new DomainException("source_invalid_text", "The teaching source contains invalid UTF-8 text.");
        }

        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var expectedTitle = entry.Source.Id switch
        {
            SortingTeachingSources.BubbleId => "Bubble sort",
            SortingTeachingSources.InsertionId => "Insertion sort",
            SortingTeachingSources.MergeId => "Merge sort",
            _ => throw Invalid()
        };
        var titles = document.QuerySelectorAll("h1");
        var sections = document.QuerySelectorAll("#lesson-details");
        if (titles.Length != 1 || SafeText(titles[0]) != expectedTitle || sections.Length != 1)
            throw Invalid();

        var section = sections[0];
        var licenceHeading = Heading(section, "Licence");
        var licence = licenceHeading.ParentElement?.NextElementSibling;
        if (licence is null ||
            !SafeText(licence).Contains("© Oak National Academy Limited", StringComparison.Ordinal) ||
            !SafeText(licence).Contains("except where otherwise stated", StringComparison.OrdinalIgnoreCase) ||
            !licence.QuerySelectorAll("a[href]").Any(link =>
                link.GetAttribute("href") == SortingTeachingSources.LicenceUrl &&
                Plain(link) == "Open Government Licence version 3.0"))
            throw Invalid();

        var heading = Heading(section, "Key learning points");
        var list = heading.NextElementSibling;
        if (list is null || list.LocalName != "ol" || list.Children.Any(child => child.LocalName != "li"))
            throw Invalid();
        var count = entry.Source.Id switch
        {
            SortingTeachingSources.BubbleId => 4,
            SortingTeachingSources.InsertionId => 2,
            _ => 1
        };
        if (list.Children.Length < count)
            throw Invalid();
        var points = list.Children.Take(count).Select(SafeText).ToList();
        if (entry.Source.Id == SortingTeachingSources.MergeId)
        {
            var keywords = Heading(section, "Keywords").NextElementSibling;
            if (keywords is null || keywords.LocalName != "ul")
                throw Invalid();
            var merging = keywords.Children.Where(item => Plain(item).StartsWith("Merging -", StringComparison.Ordinal)).ToArray();
            if (merging.Length != 1)
                throw Invalid();
            points.Add(SafeText(merging[0]));
            points.Add("Video: " + MergeTranscript(document));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return string.Join('\n', points);
    }

    private static string MergeTranscript(IDocument document)
    {
        var scripts = document.QuerySelectorAll("script")
            .Where(script => script.TextContent.Contains("transcriptSentences", StringComparison.Ordinal)).ToArray();
        if (scripts.Length != 1) throw Invalid();
        var script = scripts[0].TextContent.Trim();
        if (!script.StartsWith(FlightPrefix, StringComparison.Ordinal) || !script.EndsWith(')'))
            throw Invalid();
        try
        {
            using var invocation = JsonDocument.Parse(script[FlightPrefix.Length..^1]);
            var args = invocation.RootElement;
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2 ||
                args[0].ValueKind != JsonValueKind.Number || !args[0].TryGetInt32(out var type) || type != 1 ||
                args[1].ValueKind != JsonValueKind.String)
                throw Invalid();
            var chunk = args[1].GetString()!;
            var separator = chunk.IndexOf(':');
            if (separator <= 0 || !chunk.AsSpan(0, separator).ToString().All(char.IsAsciiDigit))
                throw Invalid();
            using var payload = JsonDocument.Parse(chunk[(separator + 1)..]);
            var transcriptObjects = TranscriptObjects(payload.RootElement).ToArray();
            if (transcriptObjects.Length != 1) throw Invalid();
            var lesson = transcriptObjects[0];
            if (StringProperty(lesson, "programmeSlug") != "computing-secondary-ks4-gcse-ocr" ||
                StringProperty(lesson, "unitSlug") != "searching-and-sorting-algorithms" ||
                StringProperty(lesson, "lessonSlug") != "merge-sort" ||
                StringProperty(lesson, "lessonTitle") != "Merge sort")
                throw Invalid();
            var transcript = Property(lesson, "transcriptSentences");
            if (transcript.ValueKind != JsonValueKind.Array || transcript.GetArrayLength() is < 4 or > 500)
                throw Invalid();
            var sentences = transcript.EnumerateArray().Select(sentence =>
                sentence.ValueKind == JsonValueKind.String ? sentence.GetString() : null).ToArray();
            if (sentences.Any(sentence => sentence is null || sentence.Length > 500)) throw Invalid();
            var starts = Enumerable.Range(0, sentences.Length)
                .Where(index => sentences[index] == MergeProcedure[0]).ToArray();
            if (starts.Length != 1 || starts[0] + MergeProcedure.Length > sentences.Length ||
                !sentences.Skip(starts[0]).Take(MergeProcedure.Length).SequenceEqual(MergeProcedure, StringComparer.Ordinal))
                throw Invalid();
            return string.Join(" ", sentences.Skip(starts[0]).Take(MergeProcedure.Length));
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static IEnumerable<JsonElement> TranscriptObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.EnumerateObject().Any(property => property.NameEquals("transcriptSentences")))
                yield return element;
            foreach (var property in element.EnumerateObject())
                foreach (var child in TranscriptObjects(property.Value))
                    yield return child;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var child in TranscriptObjects(item))
                    yield return child;
        }
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        var properties = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length != 1) throw Invalid();
        return properties[0].Value;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static IElement Heading(IElement section, string text)
    {
        var matches = section.QuerySelectorAll("h3").Where(heading => Plain(heading) == text).ToArray();
        if (matches.Length != 1) throw Invalid();
        _ = SafeText(matches[0]);
        return matches[0];
    }

    private static string SafeText(IElement element)
    {
        if (element.Closest("[hidden], [aria-hidden=true]") is not null ||
            element.QuerySelector("script, style, template, [hidden], [aria-hidden=true]") is not null)
            throw Invalid();
        var text = Plain(element);
        if (string.IsNullOrWhiteSpace(text)) throw Invalid();
        return text;
    }

    private static string Plain(IElement element) => string.Join(' ',
        element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DomainException Invalid() =>
        new("source_invalid_text", "The teaching source does not contain the expected lesson and licence information.");
}
