using System.Text;
using LessonPlanner.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace LessonPlanner.Infrastructure.Retrieval;

internal interface ICurriculumTextExtractor
{
    string Extract(byte[] payload, CurriculumRetrievalOptions options, CancellationToken cancellationToken);
}

internal sealed class BoundedPdfTextExtractor : ICurriculumTextExtractor
{
    public string Extract(byte[] payload, CurriculumRetrievalOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > options.MaximumDownloadBytes)
            throw new DomainException("source_payload_limit", "The PDF exceeds the download budget.");
        if (payload.Length < 16 || !payload.AsSpan(0, 5).SequenceEqual("%PDF-"u8) ||
            payload.AsSpan(Math.Max(0, payload.Length - 1024)).IndexOf("%%EOF"u8) < 0)
            throw new DomainException("source_invalid_pdf", "The response is not a complete PDF document.");

        var budget = new PdfExtractionBudget(options.ExtractionTimeout, cancellationToken);
        try
        {
            using var stream = new BudgetedPdfStream(payload, budget);
            using var document = PdfDocument.Open(stream, new ParsingOptions
            {
                UseLenientParsing = false,
                SkipMissingFonts = false,
                ClipPaths = false,
                MaxStackDepth = 64,
                FilterProvider = new BoundedPdfFilters(budget)
            });
            budget.Check();
            if (document.IsEncrypted)
                throw new DomainException("source_invalid_pdf", "Encrypted curriculum documents are not supported.");
            if (document.NumberOfPages < 1 || document.NumberOfPages > options.MaximumPdfPages)
                throw new DomainException("source_pdf_limit", "The PDF exceeds the page budget or has no pages.");

            var result = new StringBuilder();
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                budget.Check();
                var page = document.GetPage(pageNumber);
                budget.Check();
                if (page.Letters.Count > options.MaximumTextCharacters)
                    throw new DomainException("source_text_limit", "A PDF page exceeds the text budget.");
                var text = ContentOrderTextExtractor.GetText(page);
                if ((long)result.Length + text.Length + Environment.NewLine.Length > options.MaximumTextCharacters)
                    throw new DomainException("source_text_limit", "The curriculum text exceeds the extraction budget.");
                result.AppendLine(text);
            }
            budget.Check();
            return result.ToString();
        }
        catch (Exception exception) when (exception is PdfDocumentFormatException or PdfDocumentEncryptedException or
            InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            throw new DomainException("source_invalid_pdf", "The curriculum PDF is malformed or uses unsupported features.");
        }
    }
}
