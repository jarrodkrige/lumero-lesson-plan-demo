using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

internal sealed record CurriculumAnalysis(string Summary, ImmutableArray<string> Requirements);

// Provenance is deliberately absent from the model's output contract.
internal sealed record LessonBody(string Title, string Overview, ImmutableArray<LearningObjective> Objectives,
    ImmutableArray<string> Prerequisites, string Explanation, ImmutableArray<LessonActivity> Activities,
    ImmutableArray<string> AssessmentQuestions, ImmutableArray<string> Materials)
{
    internal LessonPlan WithSources(ImmutableArray<SourceReference> sources) =>
        new(Title, Overview, Objectives, Prerequisites, Explanation, Activities, AssessmentQuestions, Materials, sources);

    internal static LessonBody From(LessonPlan lesson) => new(lesson.Title, lesson.Overview, lesson.Objectives,
        lesson.Prerequisites, lesson.Explanation, lesson.Activities, lesson.AssessmentQuestions, lesson.Materials);
}

internal static partial class AgentJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    internal static T Parse<T>(string text, int maximumCharacters) where T : class
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumCharacters)
            throw InvalidOutput();
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = 24,
                AllowDuplicateProperties = false
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !ValidModelValues(document.RootElement))
                throw InvalidOutput();
            return document.RootElement.Deserialize<T>(Options) ?? throw InvalidOutput();
        }
        catch (JsonException)
        {
            throw InvalidOutput();
        }
    }

    internal static string Input<T>(T value, int maximumCharacters)
    {
        var json = JsonSerializer.Serialize(value, Options);
        if (json.Length > maximumCharacters)
            throw new DomainException("agent_input", "The supplied material exceeds the agent input budget.");
        return json;
    }

    internal static bool HasLink(string? value) => value is not null && LinkPattern().IsMatch(value);

    internal static bool ValidModelValues(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().All(property => ValidModelValues(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(ValidModelValues),
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !HasLink(element.GetString()) &&
            element.GetString() is { } value && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')),
        _ => true
    };

    internal static DomainException InvalidOutput() =>
        new("agent_output", "The agent returned an invalid or oversized structured response.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 24
        };
        options.Converters.Add(new JsonStringEnumConverter<ReviewSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [GeneratedRegex(@"(?:\b[a-z][a-z0-9+.-]{1,31}://|\b(?:www\.|mailto:|data:|javascript:)|\]\s*\()", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}
