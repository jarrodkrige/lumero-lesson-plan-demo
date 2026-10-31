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
        if (permit is null) return Redirect("/presenter?error=busy");
        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!int.TryParse(Field(form, "DurationMinutes", 3), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) throw InvalidForm();
            var request = new LessonRequest(Field(form, "Topic", 120), Field(form, "EducationalLevel", 40), minutes,
                Field(form, "Instructions", 1000, optional: true));
            DomainRules.Require(DomainRules.ValidateRequest(request));
            using var timeout = Timeout(context, 240);
            var lesson = await workflow.GenerateAsync(request, owner, null, timeout.Token);
            return Redirect($"/presenter/lessons/{lesson.Id}");
        }
        catch (DomainException) { return Redirect("/presenter?error=validation"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Redirect("/presenter?error=service"); }
    }

    private static async Task<IResult> Draft(string id, HttpContext context, PresenterAccess presenter, ILessonWorkflow workflow, ModelWorkGate gate)
    {
        if (!DomainRules.IsId(id)) return Results.NotFound();
        var owner = presenter.RequireIdentity();
        using var permit = gate.TryEnter(owner);
        if (permit is null) return Redirect($"/presenter/lessons/{id}?error=busy");
        try
        {
            using var timeout = Timeout(context, 180);
            var draft = await workflow.DraftQuizAsync(id, owner, null, timeout.Token);
            return Redirect($"/presenter/drafts/{draft.Id}");
        }
        catch (DomainException) { return Redirect($"/presenter/lessons/{id}?error=review"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Redirect($"/presenter/lessons/{id}?error=service"); }
    }

    private static async Task<IResult> Publish(string id, HttpContext context, PresenterAccess presenter,
        IQuizStore store, IQuizPublisherAgent publisher, ModelWorkGate gate)
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
            var receipt = await publisher.PublishAsync(new(approval.Id, id, approval.Version, approval.PayloadHash), caller, timeout.Token);
            if (receipt.Id != id || await store.FindPublishedAsync(id, context.RequestAborted) is null)
                throw new DomainException("publication_missing", "No completed publication is available.");
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
    private static IResult Redirect(string path) => new SeeOther(path);
    private sealed class SeeOther(string path) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = path;
            return Task.CompletedTask;
        }
    }
}
