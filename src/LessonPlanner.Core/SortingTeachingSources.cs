using System.Collections.Immutable;

namespace LessonPlanner.Core;

public static class SortingTeachingSources
{
    public const string OcrSpecificationId = "ocr-j277-specification";
    public const string BubbleId = "oak-bubble-sort";
    public const string InsertionId = "oak-insertion-sort";
    public const string MergeId = "oak-merge-sort";
    public const string OakUrl = "https://www.thenational.academy/";
    public const string LicenceUrl = "https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/";
    public const string Attribution = "Source: A computing lesson by Oak National Academy licensed under Open Government Licence (OGL).";

    public static CurriculumSource OcrSpecification { get; } =
        new(OcrSpecificationId, "OCR GCSE (9–1) Computer Science J277 specification",
            new("https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf"));

    public static ImmutableArray<CurriculumSource> All { get; } =
    [
        new(BubbleId, "Oak National Academy: bubble sort",
            new("https://www.thenational.academy/teachers/programmes/computing-secondary-ks4-gcse-ocr/units/searching-and-sorting-algorithms/lessons/bubble-sort-23973")),
        new(InsertionId, "Oak National Academy: insertion sort",
            new("https://www.thenational.academy/teachers/programmes/computing-secondary-ks4-gcse-ocr/units/searching-and-sorting-algorithms/lessons/insertion-sort")),
        new(MergeId, "Oak National Academy: merge sort",
            new("https://www.thenational.academy/teachers/programmes/computing-secondary-ks4-gcse-ocr/units/searching-and-sorting-algorithms/lessons/merge-sort"))
    ];

    public static ImmutableArray<CurriculumSource> Required { get; } = [OcrSpecification, .. All];

    public static bool Matches(CurriculumSource source) =>
        All.Any(entry => entry == source);

    public static bool Matches(SourceReference source) =>
        All.Any(entry => entry.Id == source.Id && entry.Url == source.Url && entry.Title == source.Title);

    public static ImmutableArray<CurriculumSource> Attributions(IEnumerable<SourceReference> sources) =>
        All.Where(entry => sources.Any(source => entry.Id == source.Id &&
            entry.Title == source.Title && entry.Url == source.Url)).ToImmutableArray();
}
