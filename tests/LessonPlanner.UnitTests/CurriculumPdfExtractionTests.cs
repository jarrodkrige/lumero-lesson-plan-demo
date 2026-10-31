using System.IO.Compression;
using System.Text;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Retrieval;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace LessonPlanner.UnitTests;

public sealed class CurriculumPdfExtractionTests
{
    [Fact]
    public void MaintainedParserExtractsOriginalSyntheticCurriculumText()
    {
        var text = new BoundedPdfTextExtractor().Extract(RetrievalRenderingFixtures.Pdf(), new(), CancellationToken.None);
        var unwrapped = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(RetrievalRenderingFixtures.OcrText, unwrapped);
    }

    [Fact]
    public void RobotSportsDayCurriculumIsValidatedAsKeyStageThreeData()
    {
        var options = new CurriculumRetrievalOptions();
        var text = new BoundedPdfTextExtractor().Extract(RetrievalRenderingFixtures.Pdf(RetrievalRenderingFixtures.DfeText), options, CancellationToken.None);
        var normalized = CurriculumText.ValidateAndNormalize(text, CuratedCurriculumCatalogue.Find(CuratedCurriculumCatalogue.DfeComputingKeyStages3And4), options, CancellationToken.None);
        Assert.Contains("robots", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>J277 algorithms sorting is not a PDF</body></html>")]
    [InlineData("%PDF-1.7\ntruncated document")]
    [InlineData("%PDF-1.7\nmalformed objects\n%%EOF")]
    public void MalformedOrDisguisedDocumentsAreRejected(string content)
    {
        var exception = Assert.Throws<DomainException>(() => new BoundedPdfTextExtractor().Extract(Encoding.UTF8.GetBytes(content), new(), CancellationToken.None));
        Assert.Equal("source_invalid_pdf", exception.Code);
    }

    [Fact]
    public void PageCountIsBounded()
    {
        var exception = Assert.Throws<DomainException>(() => new BoundedPdfTextExtractor().Extract(
            RetrievalRenderingFixtures.Pdf(pages: 2), new() { MaximumPdfPages = 1 }, CancellationToken.None));
        Assert.Equal("source_pdf_limit", exception.Code);
    }

    [Fact]
    public void ExtractedPageTextIsBounded()
    {
        var exception = Assert.Throws<DomainException>(() => new BoundedPdfTextExtractor().Extract(
            RetrievalRenderingFixtures.Pdf(), new() { MaximumTextCharacters = 100, MaximumExtractCharacters = 64 }, CancellationToken.None));
        Assert.Equal("source_text_limit", exception.Code);
    }

    [Fact]
    public void PdfInputSizeIsCheckedEvenWithoutTheHttpTransport()
    {
        var exception = Assert.Throws<DomainException>(() => new BoundedPdfTextExtractor().Extract(
            RetrievalRenderingFixtures.Pdf(), new() { MaximumDownloadBytes = 32 }, CancellationToken.None));
        Assert.Equal("source_payload_limit", exception.Code);
    }

    [Fact]
    public void ParserHonorsAlreadyCancelledCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new BoundedPdfTextExtractor().Extract(
            RetrievalRenderingFixtures.Pdf(), new(), cancellation.Token));
    }

    [Fact]
    public void BoundedFlateWrapperPreservesValidStreamContent()
    {
        var input = Encoding.UTF8.GetBytes(RetrievalRenderingFixtures.OcrText);
        var provider = Filters();
        var dictionary = FilterDictionary(NameToken.FlateDecode);
        var filter = Assert.Single(provider.GetFilters(dictionary));
        var decoded = filter.Decode(Compress(input), dictionary, provider, 0);
        Assert.Equal(input, decoded.ToArray());
    }

    [Fact]
    public void CompressedBombFailsBeforeUnboundedPdfPigInflation()
    {
        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var block = new byte[8192];
            for (var index = 0; index <= PdfExtractionBudget.MaximumDecodedStreamBytes / block.Length; index++) compressor.Write(block);
        }
        Assert.InRange(compressed.Length, 1, 20_000);
        var provider = Filters();
        var dictionary = FilterDictionary(NameToken.FlateDecode);
        var exception = Assert.Throws<DomainException>(() => provider.GetFilters(dictionary)[0].Decode(compressed.ToArray(), dictionary, provider, 0));
        Assert.Equal("source_pdf_limit", exception.Code);
    }

    [Theory]
    [InlineData("Columns", 2147483647)]
    [InlineData("Columns", 0)]
    [InlineData("Colors", 200)]
    [InlineData("BitsPerComponent", 3)]
    [InlineData("Predictor", 9)]
    public void PredictorParametersCannotTriggerUnboundedAllocations(string name, int value)
    {
        var parameters = new DictionaryToken(new Dictionary<NameToken, IToken> { [NameToken.Create(name)] = new NumericToken(value) });
        var dictionary = FilterDictionary(NameToken.FlateDecode, parameters);
        var provider = Filters();
        var exception = Assert.Throws<DomainException>(() => provider.GetFilters(dictionary)[0].Decode(Compress("data"u8.ToArray()), dictionary, provider, 0));
        Assert.Equal("source_invalid_pdf", exception.Code);
    }

    [Fact]
    public void TruncatedPredictorRowsAreRejectedInsteadOfSilentlyPadded()
    {
        var parameters = new DictionaryToken(new Dictionary<NameToken, IToken>
        {
            [NameToken.Predictor] = new NumericToken(12), [NameToken.Columns] = new NumericToken(4)
        });
        var dictionary = FilterDictionary(NameToken.FlateDecode, parameters);
        var provider = Filters();
        var exception = Assert.Throws<DomainException>(() => provider.GetFilters(dictionary)[0].Decode(Compress(new byte[3]), dictionary, provider, 0));
        Assert.Equal("source_invalid_pdf", exception.Code);
    }

    [Fact]
    public void ExcessiveFilterChainsAreRejected()
    {
        var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
        {
            [NameToken.Filter] = new ArrayToken(Enumerable.Repeat<IToken>(NameToken.FlateDecode, 5).ToArray())
        });
        var exception = Assert.Throws<DomainException>(() => Filters().GetFilters(dictionary));
        Assert.Equal("source_pdf_limit", exception.Code);
    }

    [Fact]
    public void UnsupportedStreamEncodingsFailClosed()
    {
        var provider = Filters();
        var dictionary = FilterDictionary(NameToken.LzwDecode);
        var filter = Assert.Single(provider.GetFilters(dictionary));
        Assert.False(filter.IsSupported);
        var exception = Assert.Throws<DomainException>(() => filter.Decode(new byte[16], dictionary, provider, 0));
        Assert.Equal("source_invalid_pdf", exception.Code);
    }

    [Fact]
    public void DamagedCompressedDataIsNotSilentlyReturnedAsPlaintext()
    {
        var provider = Filters();
        var dictionary = FilterDictionary(NameToken.FlateDecode);
        Assert.Throws<InvalidDataException>(() => provider.GetFilters(dictionary)[0].Decode("not zlib data"u8.ToArray(), dictionary, provider, 0));
    }

    [Fact]
    public void AggregateDecompressionAndReadBudgetsAreEnforced()
    {
        var decoding = new PdfExtractionBudget(TimeSpan.FromSeconds(5), CancellationToken.None);
        for (var index = 0; index < 8; index++) decoding.ChargeDecoded(PdfExtractionBudget.MaximumDecodedStreamBytes);
        Assert.Equal("source_pdf_limit", Assert.Throws<DomainException>(() => decoding.ChargeDecoded(1)).Code);
        var reading = new PdfExtractionBudget(TimeSpan.FromSeconds(5), CancellationToken.None);
        reading.ChargeRead(128 * 1024 * 1024);
        Assert.Equal("source_pdf_limit", Assert.Throws<DomainException>(() => reading.ChargeRead(1)).Code);
    }

    [Fact]
    public void ShortEvidenceExtractDoesNotSplitUnicodeScalarValues()
    {
        var text = new string('a', 79) + "\U0001F916" + " sorting " + new string('b', 100);
        var extract = CurriculumText.ShortExtract(text, "sorting", 80);
        Assert.DoesNotContain(Rune.ReplacementChar, extract.EnumerateRunes());
        Assert.InRange(extract.Length, 1, 80);
    }

    private static BoundedPdfFilters Filters() => new(new PdfExtractionBudget(TimeSpan.FromSeconds(5), CancellationToken.None));
    private static DictionaryToken FilterDictionary(NameToken name, DictionaryToken? parameters = null)
    {
        var values = new Dictionary<NameToken, IToken> { [NameToken.Filter] = name };
        if (parameters is not null) values.Add(NameToken.DecodeParms, parameters);
        return new(values);
    }
    private static byte[] Compress(byte[] input)
    {
        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) compressor.Write(input);
        return compressed.ToArray();
    }
}
