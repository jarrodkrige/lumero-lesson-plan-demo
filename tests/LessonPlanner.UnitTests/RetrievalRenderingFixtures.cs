using System.Security.Cryptography;
using System.Text;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Retrieval;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LessonPlanner.UnitTests;

internal static class RetrievalRenderingFixtures
{
    // Original synthetic fixtures, not extracts copied from either curriculum publication.
    internal const string OcrText = "Synthetic J277 curriculum test: algorithms and sorting. " +
        "A fictional robot sports day records finish times. Learners compare adjacent entries, trace changes, " +
        "and explain how an ordered result helps select a finalist. This text is authored only for automated tests.";
    internal const string DfeText = "Synthetic computing curriculum test for key stage 3: algorithms. " +
        "Teams program imaginary robots to complete a sports day course, test a sequence, and explain a correction. " +
        "The teacher asks each team to compare their design with the observed result. This is original test text.";
    internal static byte[] TransportPayload => "%PDF-1.7\nSynthetic transport fixture, not parsed in transport-only tests.\n%%EOF"u8.ToArray();
    internal static DateTimeOffset Timestamp => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    internal static byte[] Pdf(string text = OcrText, int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var pageIndex = 0; pageIndex < pages; pageIndex++)
        {
            var page = builder.AddPage(PageSize.A4);
            var words = text.Split(' ');
            var line = new StringBuilder();
            var top = 780;
            foreach (var word in words)
            {
                if (line.Length + word.Length > 72)
                {
                    page.AddText(line.ToString(), 11, new PdfPoint(40, top), font);
                    top -= 16;
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) page.AddText(line.ToString(), 11, new PdfPoint(40, top), font);
        }
        return builder.Build();
    }

    internal static LessonRecord Lesson()
    {
        var source = new SourceReference(CuratedCurriculumCatalogue.OcrJ277Specification,
            "Synthetic test reference for OCR J277",
            CuratedCurriculumCatalogue.Sources[0].Url, Timestamp,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OcrText))), OcrText);
        var request = new LessonRequest("Sorting algorithms", "GCSE (OCR J277)", 60, "Robot sports day");
        var plan = new LessonPlan("Robot sports day: sorting algorithms",
            "Use a fictional robot sports day to compare finish times and explain the steps in a sorting algorithm.",
            [new("Trace adjacent comparisons and swaps in a bubble sort.", [source.Id]),
             new("Explain how an ordered list identifies the fastest robot.", [source.Id])],
            ["Compare integer values.", "Read an ordered sequence."],
            "The robots have recorded integer finish times. Compare two adjacent times, swap out-of-order values, " +
            "and explain how repeated passes produce a sorted sequence. Compare a worked example with a new input.",
            [new("Robot qualifying", 10, "Predict the order of five fictional finish times before discussing a sorting rule.", "Explain a comparison to a partner."),
             new("Trace the sorting rounds", 25, "Work in pairs to trace comparisons and swaps using robot time cards.", "Check every swap against the ordering rule."),
             new("Independent challenge", 20, "Trace a fresh set of times and justify the final order with a short explanation.", "Inspect the trace and justification."),
             new("Exit assessment", 5, "Answer the exit questions and describe one remaining uncertainty.", "Collect an individual explanation.")],
            ["Which comparison happens first, and why?", "How can you tell that another pass is unnecessary?"],
            ["Fictional robot time cards", "Paper and pens"], [source]);
        return new(DomainRules.NewId(), new(Guid.NewGuid(), Guid.NewGuid()), request, plan,
            new(true, []), RunStatus.Ready, [], Timestamp);
    }
}
