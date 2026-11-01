using System.Globalization;
using LessonPlanner.Core;

namespace LessonPlanner.Web.Components;

public static class DisplayText
{
    private static readonly CultureInfo BritishEnglish = CultureInfo.GetCultureInfo("en-GB");

    public static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", BritishEnglish);

    public static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static bool IsSafeSourceUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo);

    public static string Role(AgentRole role) => role switch
    {
        AgentRole.Orchestrator => "Orchestrator",
        AgentRole.Curriculum => "Curriculum",
        AgentRole.LessonWriter => "Writer",
        AgentRole.Reviewer => "Reviewer",
        AgentRole.QuizPublisher => "Quiz Publisher",
        AgentRole.Validation => "Checks",
        AgentRole.Persistence => "Save",
        _ => "Lesson workflow"
    };

    public static string State(ActivityState state) => state switch
    {
        ActivityState.Started => "Started",
        ActivityState.Succeeded => "Completed",
        ActivityState.Failed => "Failed",
        ActivityState.RevisionRequested => "Revision requested",
        ActivityState.AwaitingApproval => "Awaiting approval",
        _ => "Recorded"
    };

    public static string Severity(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => "Needs attention",
        ReviewSeverity.Revision => "Revision needed",
        _ => "Review note"
    };
}
