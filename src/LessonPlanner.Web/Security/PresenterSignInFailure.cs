using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Client;

namespace LessonPlanner.Web.Security;

internal static class PresenterSignInFailure
{
    public static Task HandleAsync(RemoteFailureContext context)
    {
        var reference = Guid.NewGuid().ToString("N");
        string? providerCorrelationId = null;
        Exception? failure = context.Failure;
        for (var depth = 0; failure is not null && depth < 8; depth++, failure = failure.InnerException)
        {
            if (failure is MsalException msal && Guid.TryParse(msal.CorrelationId, out var correlation) && correlation != Guid.Empty)
            {
                providerCorrelationId = correlation.ToString("D");
                break;
            }
        }
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LessonPlanner.Web.Security.PresenterSignIn");
        logger.LogWarning(new EventId(1001, "PresenterSignInFailed"),
            "Presenter authentication failed. Reference {Reference}; provider correlation {ProviderCorrelationId}; failure type {FailureType}.",
            reference, providerCorrelationId, context.Failure?.GetType().Name ?? "Unknown");
        context.HandleResponse();
        context.Response.Redirect("/sign-in-failed?reference=" + reference);
        return Task.CompletedTask;
    }
}
