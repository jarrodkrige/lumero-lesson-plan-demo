using System.Collections.Immutable;
using System.Globalization;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using LessonPlanner.Web.Security;
using LessonPlanner.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace LessonPlanner.Web.Endpoints;

public static class LessonEndpoints
{
    public static WebApplication MapLessonEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/sign-in", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/presenter" },
            [OpenIdConnectDefaults.AuthenticationScheme])).RequireRateLimiting("public");
        app.MapPost("/auth/sign-out", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme])).RequireAuthorization("Presenter").AddEndpointFilter<AntiforgeryFilter>();
        var presenter = app.MapGroup("/presenter").RequireAuthorization("Presenter");
        presenter.MapPost("/lessons", Generate).AddEndpointFilter<AntiforgeryFilter>().RequireRateLimiting("model");
        presenter.MapPost("/lessons/{id}/quiz", Draft).AddEndpointFilter<AntiforgeryFilter>().RequireRateLimiting("model");
        presenter.MapPost("/drafts/{id}/publish", Publish).AddEndpointFilter<AntiforgeryFilter>().RequireRateLimiting("model");
        presenter.MapGet("/lessons/{id}/document", Document).RequireRateLimiting("public");
        presenter.MapGet("/drafts/{id}/qr", Qr).RequireRateLimiting("public");
        app.MapPost("/quiz/{id}/submit", Submit).AllowAnonymous().AddEndpointFilter<AntiforgeryFilter>().RequireRateLimiting("public");
        return app;
    }

    private static async Task<IResult> Generate(HttpContext context, PresenterAccess presenter, ILessonWorkflow workflow, ModelWorkGate gate)
    {
        var owner = presenter.RequireIdentity();
        using var permit = gate.TryEnter(owner);
        if (permit is null) return ActionRedirect("/presenter?error=busy",
            "The lesson service is busy. Wait a moment before trying again.", 409);
        try
        {
            using var timeout = Timeout(context, 210);
            var form = await context.Request.ReadFormAsync(timeout.Token);
            if (!int.TryParse(Field(form, "DurationMinutes", 3), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) throw InvalidForm();
            var request = new LessonRequest(Field(form, "Topic", 120), Field(form, "EducationalLevel", 40), minutes,
                Field(form, "Instructions", 1000, optional: true));
            DomainRules.Require(DomainRules.ValidateRequest(request));
            var lesson = await workflow.GenerateAsync(request, owner, null, timeout.Token);
            return ActionRedirect($"/presenter/lessons/{lesson.Id}");
        }
        catch (DomainException error) when (error.Code == "sorting_example") { return ActionRedirect("/presenter?error=sorting",
            "Use one example list with two to eight whole numbers separated by commas or 'and'.", 422); }
        catch (DomainException) { return ActionRedirect("/presenter?error=validation",
            "Check the topic, educational level and lesson length, then try again.", 422); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return ActionRedirect("/presenter?error=service",
            "The lesson request timed out. Completion could not be confirmed. No request has been repeated.", 503); }
    }

    private static async Task<IResult> Draft(string id, HttpContext context, PresenterAccess presenter, ILessonWorkflow workflow, ModelWorkGate gate)
    {
        if (!DomainRules.IsId(id)) return Results.NotFound();
        var owner = presenter.RequireIdentity();
        using var permit = gate.TryEnter(owner);
        if (permit is null) return ActionRedirect($"/presenter/lessons/{id}?error=busy",
            "The quiz service is busy. Wait a moment before trying again.", 409);
        try
        {
            using var timeout = Timeout(context, 180);
            var draft = await workflow.DraftQuizAsync(id, owner, null, timeout.Token);
            return ActionRedirect($"/presenter/drafts/{draft.Id}");
        }
        catch (DomainException error)
        {
            var needsReview = error.Code is "lesson_not_ready" or "not_found" or "quiz_rejected";
            return ActionRedirect($"/presenter/lessons/{id}?error={(needsReview ? "review" : "service")}",
                needsReview
                    ? "The quiz could not pass its checks or the lesson is unavailable for quiz creation. Read the lesson review before trying again."
                    : "The quiz draft could not be completed. Your lesson is still available. Try again in a moment.", needsReview ? 422 : 503);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return ActionRedirect($"/presenter/lessons/{id}?error=service",
            "The quiz request timed out. Completion could not be confirmed. Your lesson is still available. No request has been repeated.", 503); }
    }

    private static async Task<IResult> Publish(string id, HttpContext context, PresenterAccess presenter,
        IQuizStore store, IQuizPublisherAgent publisher, ModelWorkGate gate, ILoggerFactory loggers)
    {
        if (!DomainRules.IsId(id)) return Results.NotFound();
        var caller = presenter.RequireCaller();
        using var permit = gate.TryEnter(caller.Presenter);
        if (permit is null) return Redirect($"/presenter/drafts/{id}?error=busy");
        try
        {
            var draft = await store.FindDraftAsync(id, caller.Presenter, context.RequestAborted);
            if (draft is null) return Results.NotFound();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (Field(form, "acknowledged", 3) != "yes" ||
                !int.TryParse(Field(form, "version", 4), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                version != draft.Version || Field(form, "payloadHash", 64) != draft.PayloadHash) throw InvalidForm();
            if (await store.FindPublishedAsync(id, context.RequestAborted) is not null)
                return Redirect($"/presenter/drafts/{id}");
            var approval = await store.ApproveAsync(id, caller.Presenter, caller.ClientId, context.RequestAborted);
            using var timeout = Timeout(context, 120);
            try
            {
                var receipt = await publisher.PublishAsync(new(approval.Id, id, approval.Version, approval.PayloadHash), caller, timeout.Token);
                if (receipt.Id != id || await store.FindPublishedAsync(id, timeout.Token) is null)
                    throw new DomainException("publication_missing", "No completed publication is available.");
            }
            catch (Exception error) when (!context.RequestAborted.IsCancellationRequested)
            {
                // The tool may commit before a model or transport loses its final response.
                using var confirmation = Timeout(context, 10);
                if (await store.FindPublishedAsync(id, confirmation.Token) is null) throw;
                loggers.CreateLogger(typeof(LessonEndpoints)).LogWarning(
                    "Publication confirmed in durable storage after {ExceptionType}; no action was repeated.", error.GetType().Name);
            }
            return Redirect($"/presenter/drafts/{id}");
        }
        catch (DomainException) { return Redirect($"/presenter/drafts/{id}?error=approval"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Redirect($"/presenter/drafts/{id}?error=publication"); }
    }

    private static async Task<IResult> Document(string id, HttpContext context, PresenterAccess presenter,
        ILessonStore store, ILessonDocumentRenderer renderer)
    {
        if (!DomainRules.IsId(id)) return Results.NotFound();
        var lesson = await store.FindAsync(id, presenter.RequireIdentity(), context.RequestAborted);
        if (lesson?.Status != RunStatus.Ready) return Results.NotFound();
        var document = renderer.Render(lesson);
        return Results.File(document.Content, document.ContentType, document.FileName);
    }

    private static async Task<IResult> Qr(string id, HttpContext context, PresenterAccess presenter, IQuizStore store,
        IQuizQrRenderer renderer, QuizPolicy policy, QuizLinkVerifier links)
    {
        if (!DomainRules.IsId(id) || await store.FindDraftAsync(id, presenter.RequireIdentity(), context.RequestAborted) is null ||
            await store.FindPublishedAsync(id, context.RequestAborted) is null) return Results.NotFound();
        if (!await links.IsReachableAsync(id, context.RequestAborted))
            return Results.Problem("The canonical quiz page is not currently reachable. Check the public address before displaying its QR code.", statusCode: 503);
        return Results.File(renderer.Render(policy.PublicUrl(id)), "image/png");
    }

    private static async Task<IResult> Submit(string id, HttpContext context, IQuizStore store, ParticipantTickets tickets)
    {
        if (!DomainRules.IsId(id)) return Results.NotFound();
        try
        {
            var quiz = await store.FindPublishedAsync(id, context.RequestAborted);
            if (quiz is null) return Results.NotFound();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var attemptId = Field(form, "attempt", 48);
            tickets.Validate(Field(form, "ticket", 4096), attemptId, id);
            var expected = quiz.Questions.Select(question => $"answer-{question.Id}")
                .Concat(["attempt", "ticket", "__RequestVerificationToken"]).ToHashSet(StringComparer.Ordinal);
            if (form.Keys.Any(key => !expected.Contains(key))) throw InvalidForm();
            var answers = quiz.Questions.Select(question => new QuizAnswer(question.Id, Field(form, $"answer-{question.Id}", 40))).ToImmutableArray();
            var result = await store.SubmitAsync(new(attemptId, id, answers), context.RequestAborted);
            return Redirect($"/quiz/{id}/results/{result.AttemptId}");
        }
        catch (DomainException error)
        {
            var reason = error.Code switch
            {
                "attempt_expired" or "quiz_closed" => "expired",
                "quiz_full" => "full",
                "busy" => "service",
                _ => "answers"
            };
            return Redirect($"/quiz/{id}?error={reason}");
        }
    }

    private static string Field(IFormCollection form, string name, int maximum, bool optional = false)
    {
        var values = form[name];
        if (optional && values.Count == 0) return "";
        if (values.Count != 1 || values[0] is not { } value || value.Length > maximum || (!optional && string.IsNullOrWhiteSpace(value)))
            throw InvalidForm();
        return value.Trim();
    }

    private static CancellationTokenSource Timeout(HttpContext context, int seconds)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return timeout;
    }

    private static DomainException InvalidForm() => new("invalid_form", "Check the values in the application form.");
    private static IResult Redirect(string path) => new SeeOther(path, null, 400, false);
    private static IResult ActionRedirect(string path, string? message = null, int errorStatus = 400) => new SeeOther(path, message, errorStatus, true);
    private sealed class SeeOther(string path, string? message, int errorStatus, bool enhanced) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (enhanced && httpContext.Request.Headers["X-Lumero-Action"] == "fetch")
            {
                httpContext.Response.StatusCode = message is null ? StatusCodes.Status200OK : errorStatus;
                return httpContext.Response.WriteAsJsonAsync(new { location = message is null ? path : null, message },
                    httpContext.RequestAborted);
            }
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = path;
            return Task.CompletedTask;
        }
    }
}
