namespace LessonPlanner.Infrastructure.Retrieval;

public sealed record CurriculumRetrievalOptions
{
    public int MaximumDownloadBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumTextCharacters { get; init; } = 180_000;
    public int MaximumPdfPages { get; init; } = 100;
    public int MaximumRedirects { get; init; } = 3;
    public int MaximumExtractCharacters { get; init; } = 512;
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (MaximumDownloadBytes is < 32 or > 16 * 1024 * 1024 ||
            MaximumTextCharacters is < 100 or > 250_000 || MaximumPdfPages is < 1 or > 128 ||
            MaximumRedirects is < 0 or > 5 || MaximumExtractCharacters is < 32 or > 800 ||
            MaximumExtractCharacters > MaximumTextCharacters ||
            TotalTimeout <= TimeSpan.Zero || TotalTimeout > TimeSpan.FromMinutes(1) ||
            ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TotalTimeout ||
            ExtractionTimeout <= TimeSpan.Zero || ExtractionTimeout > TotalTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(CurriculumRetrievalOptions), "Retrieval limits are outside the supported safety bounds.");
        }
    }
}
