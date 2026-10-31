using Microsoft.AspNetCore.Antiforgery;

namespace LessonPlanner.Web.Endpoints;

public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.HasFormContentType)
            return Results.BadRequest("Submit this action using its application form.");
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("This form has expired or is not from this browser. Reload the page before trying again.");
        }
        return await next(context);
    }
}
