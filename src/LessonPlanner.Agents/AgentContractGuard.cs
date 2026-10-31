using System.Collections.Immutable;
using System.Text.Json;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

internal sealed record SourceEvidence(string Id, string Title, string Extract);

internal static class AgentContractGuard
{
    internal static void RequireCatalogue(IReadOnlyList<CurriculumSource> catalogue)
    {
        if (catalogue is null || catalogue.Count is < 1 or > 50 ||
            catalogue.Any(source => source is null || !Identifier(source.Id, 50) || !Text(source.Title, 1, 200) || !Https(source.Url)) ||
            catalogue.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count() != catalogue.Count)
            throw new DomainException("curriculum_catalogue", "The curriculum source catalogue is unavailable or invalid.");
    }

    internal static void RequirePlan(OrchestrationPlan? plan, IReadOnlyList<CurriculumSource> catalogue)
    {
        RequireCatalogue(catalogue);
        if (plan is null || plan.SourceIds.IsDefaultOrEmpty || plan.SourceIds.Length > 4 ||
            plan.SourceIds.Distinct(StringComparer.Ordinal).Count() != plan.SourceIds.Length ||
            plan.SourceIds.Any(id => !catalogue.Any(source => string.Equals(source.Id, id, StringComparison.Ordinal))) ||
            !Text(plan.LearningFocus, 3, 1500) || !Text(plan.TeachingApproach, 3, 1500) ||
            AgentJson.HasLink(plan.LearningFocus) || AgentJson.HasLink(plan.TeachingApproach))
            throw AgentJson.InvalidOutput();
    }

    internal static void RequireSources(ImmutableArray<SourceReference> sources, TimeProvider clock)
    {
        if (sources.IsDefaultOrEmpty || sources.Length > 4 ||
            sources.Any(source => source is null || !Identifier(source.Id, 50) || !Text(source.Title, 1, 200) ||
                !Text(source.Extract, 1, 800) || !Https(source.Url) || !Sha256(source.Sha256) ||
                source.RetrievedAt == default || source.RetrievedAt > clock.GetUtcNow().AddMinutes(5)) ||
            sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new DomainException("curriculum_provenance", "Retrieved source provenance is missing or invalid.");
    }

    internal static void RequireSourceBinding(SourceReference reference, CurriculumSource entry)
    {
        if (reference.Id != entry.Id || reference.Url != entry.Url || reference.Title != entry.Title)
            throw new DomainException("curriculum_provenance", "Retrieved evidence does not match its selected catalogue entry.");
    }

    internal static void RequireCurriculum(CurriculumBrief? brief, TimeProvider clock, OrchestrationPlan? plan = null)
    {
        if (brief is null || !Text(brief.Summary, 20, 4000) || AgentJson.HasLink(brief.Summary) ||
            brief.Requirements.IsDefaultOrEmpty || brief.Requirements.Length > 12 ||
            brief.Requirements.Any(item => !Text(item, 5, 800) || AgentJson.HasLink(item)))
            throw AgentJson.InvalidOutput();
        RequireSources(brief.Sources, clock);
        if (plan is not null && !brief.Sources.Select(source => source.Id).SequenceEqual(plan.SourceIds, StringComparer.Ordinal))
            throw new DomainException("curriculum_provenance", "Curriculum sources do not match the selected catalogue entries.");
    }

    internal static ValidationResult Lesson(LessonPlan? lesson, LessonRequest request)
    {
        var result = DomainRules.ValidateLesson(lesson, request);
        if (!result.IsValid) return result;
        return AgentJson.ValidModelValues(JsonSerializer.SerializeToElement(LessonBody.From(lesson!), AgentJson.Options))
            ? result : new(["Lesson text must not contain links, null values or control characters."]);
    }

    internal static ValidationResult Quiz(QuizContent? quiz, ImmutableArray<SourceReference> sources)
    {
        var result = DomainRules.ValidateQuiz(quiz, sources);
        if (!result.IsValid) return result;
        return AgentJson.ValidModelValues(JsonSerializer.SerializeToElement(quiz, AgentJson.Options))
            ? result : new(["Quiz text must not contain links, null values or control characters."]);
    }

    internal static void RequireReview(ReviewResult? review)
    {
        if (!DomainRules.ValidateReview(review).IsValid || review is null ||
            (!review.Accepted && review.Findings.IsEmpty) ||
            review.Findings.Any(finding => finding is null || !Text(finding.Code, 1, 64) ||
                !Text(finding.Message, 5, 700) || !Enum.IsDefined(finding.Severity) ||
                AgentJson.HasLink(finding.Code) || AgentJson.HasLink(finding.Message)))
            throw AgentJson.InvalidOutput();
    }

    internal static ImmutableArray<SourceEvidence> Evidence(ImmutableArray<SourceReference> sources) =>
        sources.Select(source => new SourceEvidence(source.Id, source.Title, source.Extract)).ToImmutableArray();

    internal static bool Text(string? value, int minimum, int maximum) => value is not null &&
        value.Trim().Length >= minimum && value.Length <= maximum &&
        !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    private static bool Identifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    internal static bool Sha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');

    internal static bool Https(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.OriginalString.Length <= 2048;
}
