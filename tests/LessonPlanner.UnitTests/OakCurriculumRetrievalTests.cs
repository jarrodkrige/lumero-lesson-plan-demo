using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Retrieval;

namespace LessonPlanner.UnitTests;

public sealed class OakCurriculumRetrievalTests
{
    private static readonly string[] MergeSentences =
    [
        "Three, repeat steps A to B until one of the lists is empty.",
        "A, compare the first items of the two lists.",
        "B, remove the items that is lower and place it into the merged list in the next available position.",
        "Four, then place each item from the remaining list into the merged list in order."
    ];

    [Theory]
    [InlineData(SortingTeachingSources.BubbleId, "Bubble sort")]
    [InlineData(SortingTeachingSources.InsertionId, "Insertion sort")]
    [InlineData(SortingTeachingSources.MergeId, "Merge sort")]
    public async Task VerifiedHtmlProducesBoundedEvidenceAndTheHashOfTheRawPayload(string id, string title)
    {
        var payload = Encoding.UTF8.GetBytes(Fixture(id));
        var handler = new StubHandler((_, _) => Task.FromResult(HtmlResponse(payload)));
        using var retriever = Retriever(handler);
        var result = await retriever.RetrieveAsync(id, CancellationToken.None);
        var expected = SortingTeachingSources.All.Single(source => source.Id == id);
        Assert.Equal(expected.Id, result.Reference.Id);
        Assert.Equal(expected.Title, result.Reference.Title);
        Assert.Equal(expected.Url, result.Reference.Url);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Reference.Sha256);
        Assert.Equal(RetrievalRenderingFixtures.Timestamp, result.Reference.RetrievedAt);
        Assert.Equal(result.Content.Replace('\n', ' '), result.Reference.Extract);
        Assert.InRange(result.Reference.Extract.Length, 100, 512);
        Assert.Contains(title, result.Content, StringComparison.OrdinalIgnoreCase);
        if (id == SortingTeachingSources.MergeId)
        {
            Assert.Contains("Video: " + string.Join(" ", MergeSentences), result.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("Unselected transcript sentence.", result.Content, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("©", result.Content, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expected.Url, request.Url);
        Assert.Equal("text/html", request.Accept);
        Assert.Equal("identity", request.AcceptEncoding);
        Assert.False(request.HasCookie);
    }

    [Theory]
    [InlineData("application/pdf", "utf-8", null)]
    [InlineData("text/html", null, null)]
    [InlineData("text/html", "iso-8859-1", null)]
    [InlineData("text/html", "utf-8", "gzip")]
    public async Task HtmlRequiresTheExpectedMimeCharsetAndIdentityEncoding(string mediaType, string? charset, string? encoding)
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = HtmlResponse(Encoding.UTF8.GetBytes(Fixture(SortingTeachingSources.BubbleId)));
            response.Content.Headers.ContentType = new(mediaType) { CharSet = charset };
            if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(response);
        });
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
        Assert.Equal("source_invalid_response", error.Code);
    }

    [Fact]
    public async Task MalformedUtf8IsRejectedRatherThanReplaced()
    {
        var payload = Encoding.UTF8.GetBytes(Fixture(SortingTeachingSources.BubbleId)).Concat(new byte[] { 0xc3, 0x28 }).ToArray();
        var handler = new StubHandler((_, _) => Task.FromResult(HtmlResponse(payload)));
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
        Assert.Equal("source_invalid_text", error.Code);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("duplicate-title")]
    [InlineData("missing-section")]
    [InlineData("missing-heading")]
    [InlineData("duplicate-heading")]
    [InlineData("missing-point")]
    [InlineData("wrong-licence")]
    [InlineData("missing-licence-exception")]
    [InlineData("hidden-title")]
    [InlineData("hidden-section")]
    [InlineData("hidden-heading")]
    [InlineData("hidden-licence")]
    [InlineData("hidden-point")]
    [InlineData("hidden-child")]
    [InlineData("script-child")]
    public async Task ChangedOrHiddenIdentityLicenceAndTeachingMarkupFailClosed(string change)
    {
        var html = Fixture(SortingTeachingSources.BubbleId);
        html = change switch
        {
            "title" => html.Replace("<h1>Bubble sort</h1>", "<h1>Insertion sort</h1>", StringComparison.Ordinal),
            "duplicate-title" => html.Replace("<h1>Bubble sort</h1>", "<h1>Bubble sort</h1><h1>Bubble sort</h1>", StringComparison.Ordinal),
            "missing-section" => html.Replace("id=\"lesson-details\"", "id=\"other\"", StringComparison.Ordinal),
            "missing-heading" => html.Replace("Key learning points", "Other information", StringComparison.Ordinal),
            "duplicate-heading" => html.Replace("<h3>Key learning points</h3>", "<h3>Key learning points</h3><h3>Key learning points</h3>", StringComparison.Ordinal),
            "missing-point" => html.Replace("<li>The pass without a swap shows that the list is sorted and the process can stop.</li>", "", StringComparison.Ordinal),
            "wrong-licence" => html.Replace(SortingTeachingSources.LicenceUrl, "https://example.test/other", StringComparison.Ordinal),
            "missing-licence-exception" => html.Replace("except where otherwise stated", "without exception", StringComparison.Ordinal),
            "hidden-title" => html.Replace("<h1>", "<h1 hidden>", StringComparison.Ordinal),
            "hidden-section" => html.Replace("id=\"lesson-details\"", "id=\"lesson-details\" aria-hidden=\"true\"", StringComparison.Ordinal),
            "hidden-heading" => html.Replace("<h3>Key learning points", "<h3 hidden>Key learning points", StringComparison.Ordinal),
            "hidden-licence" => html.Replace("<div>©", "<div hidden>©", StringComparison.Ordinal),
            "hidden-point" => html.Replace("<li>Bubble sort", "<li hidden>Bubble sort", StringComparison.Ordinal),
            "hidden-child" => html.Replace("Bubble sort works", "<span hidden>secret</span> Bubble sort works", StringComparison.Ordinal),
            "script-child" => html.Replace("Bubble sort works", "<script>secret</script> Bubble sort works", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        var handler = new StubHandler((_, _) => Task.FromResult(HtmlResponse(Encoding.UTF8.GetBytes(html))));
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
        Assert.Equal("source_invalid_text", error.Code);
    }

    [Fact]
    public async Task MergeEvidenceRequiresTheSelectedMergingKeyword()
    {
        var html = Fixture(SortingTeachingSources.MergeId).Replace("Merging -", "Combining -", StringComparison.Ordinal);
        var handler = new StubHandler((_, _) => Task.FromResult(HtmlResponse(Encoding.UTF8.GetBytes(html))));
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.MergeId, CancellationToken.None));
        Assert.Equal("source_invalid_text", error.Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-script")]
    [InlineData("invalid-wrapper")]
    [InlineData("malformed-json")]
    [InlineData("wrong-type")]
    [InlineData("wrong-programme")]
    [InlineData("wrong-unit")]
    [InlineData("wrong-lesson")]
    [InlineData("wrong-title")]
    [InlineData("duplicate-record")]
    [InlineData("changed-procedure")]
    [InlineData("reordered-procedure")]
    [InlineData("duplicate-procedure")]
    [InlineData("non-string-sentence")]
    public async Task MergeTranscriptRequiresOneBoundUnambiguousAndUnchangedProcedure(string change)
    {
        var original = MergeFlight();
        var replacement = change switch
        {
            "missing" => "",
            "duplicate-script" => original + original,
            "invalid-wrapper" => original.Replace("self.__next_f.push(", "unexpected(", StringComparison.Ordinal),
            "malformed-json" => original.Replace("])</script>", ")</script>", StringComparison.Ordinal),
            "wrong-type" => MergeFlight(type: 0),
            "wrong-programme" => MergeFlight(programme: "another-programme"),
            "wrong-unit" => MergeFlight(unit: "another-unit"),
            "wrong-lesson" => MergeFlight(lesson: "insertion-sort"),
            "wrong-title" => MergeFlight(title: "Insertion sort"),
            "duplicate-record" => MergeFlight(duplicate: true),
            "changed-procedure" => MergeFlight(sentences: MergeSentences.Select((sentence, index) =>
                index == 1 ? sentence.Replace("compare", "ignore", StringComparison.Ordinal) : sentence).ToArray()),
            "reordered-procedure" => MergeFlight(sentences: [MergeSentences[0], MergeSentences[2], MergeSentences[1], MergeSentences[3]]),
            "duplicate-procedure" => MergeFlight(sentences: [.. MergeSentences, .. MergeSentences]),
            "non-string-sentence" => MergeFlight(sentences: [.. MergeSentences, 4]),
            _ => throw new InvalidOperationException()
        };
        var html = Fixture(SortingTeachingSources.MergeId).Replace(original, replacement, StringComparison.Ordinal);
        var handler = new StubHandler((_, _) => Task.FromResult(HtmlResponse(Encoding.UTF8.GetBytes(html))));
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.MergeId, CancellationToken.None));
        Assert.Equal("source_invalid_text", error.Code);
    }

    [Fact]
    public async Task RedirectsCannotSwitchBetweenAllowlistedLessons()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = SortingTeachingSources.All[1].Url }
        }));
        using var retriever = Retriever(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
        Assert.Equal("source_url_rejected", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HtmlDownloadAndExtractLimitsFailWithoutReturningProvenance()
    {
        var payload = Encoding.UTF8.GetBytes(Fixture(SortingTeachingSources.BubbleId));
        var tooLarge = new StubHandler((_, _) => Task.FromResult(HtmlResponse(new byte[1024 * 1024 + 1])));
        using (var retriever = Retriever(tooLarge))
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
            Assert.Equal("source_payload_limit", error.Code);
        }
        var tooLong = new StubHandler((_, _) => Task.FromResult(HtmlResponse(payload)));
        using (var retriever = Retriever(tooLong, new() { MaximumExtractCharacters = 32 }))
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                retriever.RetrieveAsync(SortingTeachingSources.BubbleId, CancellationToken.None));
            Assert.Equal("source_text_limit", error.Code);
        }
    }

    private static CuratedCurriculumRetriever Retriever(StubHandler handler, CurriculumRetrievalOptions? options = null) =>
        new(options ?? new(), new FixedTimeProvider(), handler, null);

    private static HttpResponseMessage HtmlResponse(byte[] payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        return response;
    }

    private static string Fixture(string id)
    {
        var (title, points) = id switch
        {
            SortingTeachingSources.BubbleId => ("Bubble sort", """
                <li>Bubble sort works on adjacent values and compares each pair from left to right.</li>
                <li>It swaps a pair when the two values are in the wrong order.</li>
                <li>Every pass repeats this procedure across the list until no swaps are required.</li>
                <li>The pass without a swap shows that the list is sorted and the process can stop.</li>
                """),
            SortingTeachingSources.InsertionId => ("Insertion sort", """
                <li>Insertion sort maintains a sorted sublist and an unsorted sublist during processing.</li>
                <li>Select the next unsorted item and move it into the correct position in the sorted sublist.</li>
                """),
            SortingTeachingSources.MergeId => ("Merge sort", """
                <li>Merge sort repeatedly splits a list into smaller lists, then joins those parts in order.</li>
                """),
            _ => throw new InvalidOperationException()
        };
        var flight = id == SortingTeachingSources.MergeId ? MergeFlight() : "";
        return $"""
            <!doctype html><html lang="en"><body><h1>{title}</h1>
            <section id="lesson-details">
            <h3>Key learning points</h3><ol>{points}</ol>
            <h3>Keywords</h3><ul><li>Merging - The merging step compares entries from each sorted list and joins them in order.</li></ul>
            <div><h3>Licence</h3></div>
            <div>© Oak National Academy Limited (2025), licensed under the <a href="{SortingTeachingSources.LicenceUrl}">Open Government Licence version 3.0</a>, except where otherwise stated.</div>
            </section>{flight}</body></html>
            """;
    }

    private static string MergeFlight(object?[]? sentences = null, string programme = "computing-secondary-ks4-gcse-ocr",
        string unit = "searching-and-sorting-algorithms", string lesson = "merge-sort", string title = "Merge sort",
        bool duplicate = false, int type = 1)
    {
        var transcript = sentences ?? [.. MergeSentences, "Unselected transcript sentence."];
        var record = new { programmeSlug = programme, unitSlug = unit, lessonSlug = lesson, lessonTitle = title,
            transcriptSentences = transcript };
        object?[] children = duplicate ? [record, record] : [null, new object?[] { "$", "$L51", null, record }];
        var payload = JsonSerializer.Serialize(new object?[] { "$", "$L51", null, new { children } });
        var invocation = JsonSerializer.Serialize(new object[] { type, "47:" + payload });
        return $"<script>self.__next_f.push({invocation})</script>";
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => RetrievalRenderingFixtures.Timestamp;
    }

    private sealed record RequestSnapshot(Uri? Url, string Accept, string AcceptEncoding, bool HasCookie);
    private sealed class StubHandler(Func<CancellationToken, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri, string.Join(",", request.Headers.Accept),
                string.Join(",", request.Headers.AcceptEncoding), request.Headers.Contains("Cookie")));
            return respond(cancellationToken, Requests.Count);
        }
    }
}
