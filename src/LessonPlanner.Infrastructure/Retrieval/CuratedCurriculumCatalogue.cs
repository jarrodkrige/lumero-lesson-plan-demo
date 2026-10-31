using System.Collections.Immutable;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

public static class CuratedCurriculumCatalogue
{
    public const string OcrJ277Specification = "ocr-j277-specification";
    public const string DfeComputingKeyStages3And4 = "dfe-computing-ks3-ks4";

    internal static ImmutableArray<CatalogueEntry> Entries { get; } =
    [
        new(new(OcrJ277Specification, "OCR GCSE (9–1) Computer Science J277 specification",
                new("https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf")),
            [new("https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf"),
             new("https://ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf")],
            ["J277", "algorithms", "sorting"], "sorting"),
        new(new(DfeComputingKeyStages3And4, "Department for Education: computing programmes of study, key stages 3 and 4",
                new("https://assets.publishing.service.gov.uk/media/5a7cb981ed915d682236228d/SECONDARY_national_curriculum_-_Computing.pdf")),
            [new("https://assets.publishing.service.gov.uk/media/5a7cb981ed915d682236228d/SECONDARY_national_curriculum_-_Computing.pdf")],
            ["computing", "key stage 3", "algorithms"], "key stage 3")
    ];

    public static IReadOnlyList<CurriculumSource> Sources { get; } =
        Entries.Select(entry => entry.Source).ToImmutableArray();

    internal static CatalogueEntry Find(string sourceId) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Source.Id, sourceId, StringComparison.Ordinal))
        ?? throw new DomainException("source_not_allowed", "Select a source identifier from the curated catalogue.");

    internal static bool IsAllowedHost(string host) => Entries.Any(entry =>
        entry.AllowedLocations.Any(location => string.Equals(location.IdnHost, host, StringComparison.OrdinalIgnoreCase)));
}

internal sealed record CatalogueEntry(CurriculumSource Source, ImmutableArray<Uri> AllowedLocations,
    ImmutableArray<string> IdentityMarkers, string ExtractAnchor)
{
    internal void RequireAllowedLocation(Uri location)
    {
        if (!location.IsAbsoluteUri || !location.IsWellFormedOriginalString() ||
            location.Scheme != Uri.UriSchemeHttps || location.Port != 443 ||
            location.HostNameType != UriHostNameType.Dns || location.UserInfo.Length != 0 ||
            location.Query.Length != 0 || location.Fragment.Length != 0 ||
            !AllowedLocations.Any(allowed => string.Equals(allowed.AbsoluteUri, location.AbsoluteUri, StringComparison.Ordinal)))
        {
            throw new DomainException("source_url_rejected", "The source location is not an approved HTTPS resource.");
        }
    }
}
