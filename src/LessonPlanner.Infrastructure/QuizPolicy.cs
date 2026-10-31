using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure;

public sealed record QuizPolicy(Uri PublicBaseUrl, TimeSpan Lifetime, TimeSpan ApprovalLifetime, int MaximumAttempts)
{
    public void Validate(bool allowLoopback)
    {
        if (!PublicBaseUrl.IsAbsoluteUri || !string.IsNullOrEmpty(PublicBaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(PublicBaseUrl.Query) || !string.IsNullOrEmpty(PublicBaseUrl.Fragment) ||
            PublicBaseUrl.AbsolutePath != "/" ||
            (PublicBaseUrl.Scheme != "https" && !(allowLoopback && PublicBaseUrl.IsLoopback && PublicBaseUrl.Scheme == "http")) ||
            PublicBaseUrl.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            PublicBaseUrl.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quiz:PublicBaseUrl must be the canonical HTTPS origin of the participant application.");
        if (Lifetime < TimeSpan.FromMinutes(5) || Lifetime > TimeSpan.FromDays(7) ||
            ApprovalLifetime < TimeSpan.FromSeconds(30) || ApprovalLifetime > TimeSpan.FromMinutes(10) ||
            MaximumAttempts is < 1 or > 1000)
            throw new InvalidOperationException("Quiz lifetime, approval lifetime or attempt budget is outside the supported bounds.");
    }

    public Uri PublicUrl(string quizId)
    {
        DomainRules.RequireId(quizId);
        return new(PublicBaseUrl, $"quiz/{quizId}");
    }
}
