using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace LessonPlanner.Web.Components;

public abstract class ResourcePage : ComponentBase
{
    [Inject]
    protected IHttpContextAccessor RequestContext { get; set; } = default!;

    protected CancellationToken RequestCancellation =>
        RequestContext.HttpContext?.RequestAborted ?? CancellationToken.None;

    protected void SetStatusCode(int statusCode)
    {
        if (RequestContext.HttpContext?.Response is { HasStarted: false } response)
        {
            response.StatusCode = statusCode;
        }
    }

    protected static string RouteId(string value) => Uri.EscapeDataString(value);
}
