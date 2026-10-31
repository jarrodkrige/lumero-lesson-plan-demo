using LessonPlanner.Core;
using LessonPlanner.Infrastructure;

namespace LessonPlanner.Web.Services;

public sealed class QuizLinkVerifier(QuizPolicy policy, HttpClient http)
{
    public async Task<bool> IsReachableAsync(string quizId, CancellationToken cancellationToken)
    {
        DomainRules.RequireId(quizId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync(policy.PublicUrl(quizId), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/html" ||
                !response.Headers.TryGetValues("X-Lumero-Quiz-Id", out var values) || !values.SequenceEqual([quizId], StringComparer.Ordinal))
                return false;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[4096];
            var total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token)) != 0)
                if ((total += read) > 98_304) return false;
            return total > 0;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or IOException or OperationCanceledException) { return false; }
    }
}
