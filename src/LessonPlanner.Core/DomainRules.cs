using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LessonPlanner.Core;

public static partial class DomainRules
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    public static bool IsId(string? value) => value is not null && IdPattern().IsMatch(value);
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    public static string DraftHash(QuizDraft draft) => Hash(new { draft.SchemaVersion, draft.Id, draft.LessonId, draft.Version,
        draft.Owner, draft.Content, draft.Sources, draft.Review, draft.CreatedAt });

    public static ValidationResult ValidateRequest(LessonRequest? request)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (request is null) return new(["A lesson request is required."]);
        Text(request.Topic, 3, 120, "Topic", errors);
        if (request.EducationalLevel is not ("GCSE (OCR J277)" or "Key Stage 3"))
            errors.Add("Select a supported educational level.");
        if (request.DurationMinutes is < 20 or > 120) errors.Add("Lesson duration must be between 20 and 120 minutes.");
        Text(request.Instructions ?? "", 0, 1000, "Instructions", errors);
        return new(errors.ToImmutable());
    }

    public static ValidationResult ValidateLesson(LessonPlan? lesson, LessonRequest request)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (lesson is null) return new(["A lesson is required."]);
        Text(lesson.Title, 3, 160, "Lesson title", errors);
        Text(lesson.Overview, 20, 1800, "Overview", errors);
        Text(lesson.Explanation, 40, 6000, "Explanation", errors);
        if (lesson.Objectives.IsDefaultOrEmpty || lesson.Objectives.Length > 8) errors.Add("Provide one to eight learning objectives.");
        else foreach (var objective in lesson.Objectives)
        {
            if (objective is null) { errors.Add("A learning objective is missing."); continue; }
            Text(objective.Description, 5, 500, "Learning objective", errors);
            CheckCitations(objective.SourceIds, lesson.Sources, errors);
        }
        CheckList(lesson.Prerequisites, 1, 8, 500, "Prerequisites", errors);
        CheckList(lesson.AssessmentQuestions, 2, 8, 500, "Assessment questions", errors);
        CheckList(lesson.Materials, 1, 10, 500, "Materials", errors);
        if (lesson.Activities.IsDefaultOrEmpty || lesson.Activities.Length > 10) errors.Add("Provide one to ten activities.");
        else
        {
            if (lesson.Activities.Any(a => a is null || a.DurationMinutes is < 1 or > 120) ||
                lesson.Activities.Sum(a => (long)(a?.DurationMinutes ?? 0)) != request.DurationMinutes)
                errors.Add("Activity timings must total the requested duration.");
            foreach (var activity in lesson.Activities)
            {
                if (activity is null) continue;
                Text(activity.Title, 3, 160, "Activity title", errors);
                Text(activity.Instructions, 10, 2000, "Activity instructions", errors);
                Text(activity.Assessment, 5, 700, "Activity assessment", errors);
            }
        }
        CheckSources(lesson.Sources, errors);
        if (errors.Count == 0 && JsonSerializer.Serialize(lesson, Json).Length > 28000) errors.Add("Lesson exceeds the storage budget.");
        return new(errors.ToImmutable());
    }

    public static ValidationResult ValidateQuiz(QuizContent? quiz, ImmutableArray<SourceReference> sources)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (quiz is null) return new(["A quiz is required."]);
        Text(quiz.Title, 3, 160, "Quiz title", errors);
        if (quiz.Questions.IsDefaultOrEmpty || quiz.Questions.Length != ContractVersions.QuestionCount)
            errors.Add("A quiz must contain exactly five questions.");
        else
        {
            if (quiz.Questions.Select(q => q?.Id).Distinct(StringComparer.Ordinal).Count() != ContractVersions.QuestionCount)
                errors.Add("Question identifiers must be unique.");
            if (quiz.Questions.Select(q => q?.Prompt?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ContractVersions.QuestionCount)
                errors.Add("Question prompts must be distinct.");
            foreach (var q in quiz.Questions)
            {
                if (q is null) { errors.Add("A question is missing."); continue; }
                Identifier(q.Id, 32, "Question identifier", errors);
                Text(q.Prompt, 10, 600, "Question", errors);
                Text(q.Explanation, 10, 1000, "Explanation", errors);
                if (q.Options.IsDefaultOrEmpty || q.Options.Length != 4) errors.Add("Each question needs four options.");
                else
                {
                    if (q.Options.Select(o => o?.Id).Distinct(StringComparer.Ordinal).Count() != 4 ||
                        q.Options.Select(o => o?.Text?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
                        errors.Add("Options must have distinct identifiers and text.");
                    foreach (var option in q.Options)
                    {
                        if (option is null) { errors.Add("An option is missing."); continue; }
                        Identifier(option.Id, 16, "Option identifier", errors);
                        Text(option.Text, 1, 400, "Option", errors);
                    }
                    if (q.CorrectOptionId is null || q.Options.Count(o => o is not null && o.Id == q.CorrectOptionId) != 1)
                        errors.Add("Each question needs one valid answer key.");
                }
                CheckCitations(q.SourceIds, sources, errors);
            }
        }
        CheckSources(sources, errors);
        if (errors.Count == 0 && JsonSerializer.Serialize(quiz, Json).Length > 24000) errors.Add("Quiz exceeds the storage budget.");
        return new(errors.ToImmutable());
    }

    public static ValidationResult ValidateReview(ReviewResult? review)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (review is null || review.Findings.IsDefault || review.Findings.Length > 20)
            return new(["A bounded, structured review is required."]);
        foreach (var finding in review.Findings)
        {
            if (finding is null) { errors.Add("A review finding is missing."); continue; }
            Text(finding.Code, 1, 80, "Review code", errors);
            Text(finding.Message, 1, 1000, "Review finding", errors);
            if (!Enum.IsDefined(finding.Severity)) errors.Add("Review severity is invalid.");
        }
        return new(errors.ToImmutable());
    }

    public static bool ReviewAccepted(ReviewResult? review) => review is not null && review.Accepted &&
        ValidateReview(review).IsValid && review.Findings.All(f => f.Severity == ReviewSeverity.Information);

    public static void Require(ValidationResult validation)
    {
        if (!validation.IsValid) throw new DomainException("validation", string.Join(" ", validation.Errors));
    }
    public static void RequirePresenter(PresenterIdentity? presenter)
    {
        if (presenter is null || presenter.TenantId == Guid.Empty || presenter.ObjectId == Guid.Empty)
            throw new DomainException("forbidden", "A valid presenter identity is required.");
    }
    public static void RequireId(string? id)
    {
        if (!IsId(id)) throw new DomainException("not_found", "The requested resource is unavailable.");
    }
    private static void CheckSources(ImmutableArray<SourceReference> sources, ImmutableArray<string>.Builder errors)
    {
        if (sources.IsDefaultOrEmpty || sources.Length > 4) { errors.Add("Provide one to four retrieved sources."); return; }
        if (sources.Select(s => s?.Id).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            errors.Add("Source identifiers must be unique.");
        foreach (var s in sources)
        {
            if (s is null) { errors.Add("A source is missing."); continue; }
            if (s.Url is null || !s.Url.IsAbsoluteUri || s.Url.Scheme != "https" || !string.IsNullOrEmpty(s.Url.UserInfo))
                errors.Add("Sources require absolute HTTPS URLs.");
            if (s.Sha256 is null || !HashPattern().IsMatch(s.Sha256)) errors.Add("Sources require retrieval hashes.");
            Identifier(s.Id, 50, "Source identifier", errors);
            Text(s.Title, 1, 200, "Source title", errors);
            Text(s.Extract, 1, 800, "Source extract", errors);
        }
    }
    private static void CheckCitations(ImmutableArray<string> ids, ImmutableArray<SourceReference> sources, ImmutableArray<string>.Builder errors)
    {
        if (ids.IsDefaultOrEmpty || ids.Length > 4 || sources.IsDefaultOrEmpty ||
            ids.Any(id => string.IsNullOrEmpty(id) || !sources.Any(s => s is not null && s.Id == id)))
            errors.Add("Citations must refer to retrieved sources.");
    }
    private static void CheckList(ImmutableArray<string> items, int minimum, int maximum, int length, string label, ImmutableArray<string>.Builder errors)
    {
        if (items.IsDefault || items.Length < minimum || items.Length > maximum) { errors.Add($"{label} has an invalid item count."); return; }
        foreach (var item in items) Text(item, 1, length, label, errors);
    }
    private static void Identifier(string? value, int maximum, string label, ImmutableArray<string>.Builder errors)
    {
        if (value is null || value.Length > maximum || !ContentIdPattern().IsMatch(value)) errors.Add($"{label} is invalid.");
    }
    private static void Text(string? value, int minimum, int maximum, string label, ImmutableArray<string>.Builder errors)
    {
        if (value is null || value.Trim().Length < minimum || value.Length > maximum ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            errors.Add($"{label} has an invalid length or contains control characters.");
    }
    [GeneratedRegex("^[a-f0-9]{48}$", RegexOptions.CultureInvariant)] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]*$", RegexOptions.CultureInvariant)] private static partial Regex ContentIdPattern();
}
