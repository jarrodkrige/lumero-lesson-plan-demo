using System.Reflection;
using System.Security.Claims;
using LessonPlanner.Core;
using LessonPlanner.Web.Endpoints;
using LessonPlanner.Web.Security;
using LessonPlanner.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace LessonPlanner.IntegrationTests;

public sealed class LessonEndpointCancellationTests
{
    // Invoke the handler before antiforgery can cache the form, to isolate its own cancellation budget.
    private static readonly Func<HttpContext, PresenterAccess, ILessonWorkflow, ModelWorkGate, Task<IResult>> Generate =
        typeof(LessonEndpoints).GetMethod("Generate", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<HttpContext, PresenterAccess, ILessonWorkflow, ModelWorkGate, Task<IResult>>>();

    [Fact]
    public async Task Form_read_and_generation_share_one_linked_cancellation_budget()
    {
        using var aborted = new CancellationTokenSource();
        var form = new RecordingForm();
        var context = Context(form, aborted.Token);
        var workflow = new RecordingWorkflow();
        var gate = new ModelWorkGate();

        var result = await Generate(context, Access(context), workflow, gate);
        await result.ExecuteAsync(context);

        Assert.Equal(1, form.Reads);
        Assert.True(form.Token.CanBeCanceled);
        Assert.NotEqual(context.RequestAborted, form.Token);
        Assert.Equal(form.Token, workflow.Token);
        Assert.Equal(TestData.Presenter, workflow.Presenter);
        Assert.Equal(new LessonRequest("Sorting algorithms", "GCSE (OCR J277)", 60, ""), workflow.Request);
        Assert.Null(workflow.Progress);
        Assert.Equal(StatusCodes.Status303SeeOther, context.Response.StatusCode);
        Assert.Equal($"/presenter/lessons/{workflow.Lesson.Id}", context.Response.Headers.Location.ToString());
        AssertPermitReleased(gate);
    }

    [Theory]
    [InlineData("form")]
    [InlineData("workflow")]
    public async Task Service_cancellation_redirects_only_while_the_request_remains_connected(string phase)
    {
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow();
        if (phase == "form") form.Read = token => throw new OperationCanceledException("Synthetic cancellation.", token);
        else workflow.Run = token => throw new OperationCanceledException("Synthetic cancellation.", token);
        var context = Context(form);
        var gate = new ModelWorkGate();

        var result = await Generate(context, Access(context), workflow, gate);
        await result.ExecuteAsync(context);

        Assert.False(context.RequestAborted.IsCancellationRequested);
        Assert.Equal(StatusCodes.Status303SeeOther, context.Response.StatusCode);
        Assert.Equal("/presenter?error=service", context.Response.Headers.Location.ToString());
        Assert.Equal(phase == "form" ? 0 : 1, workflow.Calls);
        AssertPermitReleased(gate);
    }

    [Theory]
    [InlineData("form")]
    [InlineData("workflow")]
    public async Task A_client_abort_cancels_the_shared_token_and_propagates_without_redirect(string phase)
    {
        using var aborted = new CancellationTokenSource();
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow();
        if (phase == "form") form.Read = token =>
        {
            aborted.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ValidForm());
        };
        else workflow.Run = token =>
        {
            aborted.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(workflow.Lesson);
        };
        var context = Context(form, aborted.Token);
        var gate = new ModelWorkGate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generate(context, Access(context), workflow, gate));

        Assert.True(form.Token.IsCancellationRequested);
        if (phase == "workflow") Assert.Equal(form.Token, workflow.Token);
        Assert.False(context.Response.HasStarted);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Equal(phase == "form" ? 0 : 1, workflow.Calls);
        AssertPermitReleased(gate);
    }

    [Theory]
    [InlineData("invalid-duration")]
    [InlineData("missing-topic")]
    [InlineData("duplicate-topic")]
    public async Task Invalid_form_still_redirects_without_generation_and_releases_the_permit(string variation)
    {
        var values = Values();
        if (variation == "invalid-duration") values["DurationMinutes"] = "bad";
        if (variation == "missing-topic") values.Remove("Topic");
        if (variation == "duplicate-topic") values["Topic"] = new StringValues(["Sorting algorithms", "Changed topic"]);
        var form = new RecordingForm { Read = _ => Task.FromResult<IFormCollection>(new FormCollection(values)) };
        var workflow = new RecordingWorkflow();
        var context = Context(form);
        var gate = new ModelWorkGate();

        var result = await Generate(context, Access(context), workflow, gate);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status303SeeOther, context.Response.StatusCode);
        Assert.Equal("/presenter?error=validation", context.Response.Headers.Location.ToString());
        Assert.Equal(0, workflow.Calls);
        AssertPermitReleased(gate);
    }

    [Fact]
    public async Task Workflow_domain_failure_keeps_the_existing_validation_redirect()
    {
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow
        {
            Run = _ => throw new DomainException("synthetic_validation", "Synthetic domain failure.")
        };
        var context = Context(form);
        var gate = new ModelWorkGate();

        var result = await Generate(context, Access(context), workflow, gate);
        await result.ExecuteAsync(context);

        Assert.Equal("/presenter?error=validation", context.Response.Headers.Location.ToString());
        AssertPermitReleased(gate);
    }

    [Theory]
    [InlineData("form")]
    [InlineData("workflow")]
    public async Task Unexpected_failure_reaches_the_existing_outer_error_boundary_and_releases_the_permit(string phase)
    {
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow();
        var failure = new InvalidOperationException("Synthetic failure.");
        if (phase == "form") form.Read = _ => throw failure;
        else workflow.Run = _ => throw failure;
        var context = Context(form);
        var gate = new ModelWorkGate();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(context, Access(context), workflow, gate)));

        Assert.False(context.Response.Headers.ContainsKey("Location"));
        AssertPermitReleased(gate);
    }

    [Fact]
    public async Task A_busy_presenter_does_not_read_the_form_or_enter_generation()
    {
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow();
        var context = Context(form);
        var gate = new ModelWorkGate();
        using var held = gate.TryEnter(TestData.Presenter);
        Assert.NotNull(held);

        var result = await Generate(context, Access(context), workflow, gate);
        await result.ExecuteAsync(context);

        Assert.Equal("/presenter?error=busy", context.Response.Headers.Location.ToString());
        Assert.Equal(0, form.Reads);
        Assert.Equal(0, workflow.Calls);
        Assert.Null(gate.TryEnter(TestData.Presenter));
    }

    [Fact]
    public async Task An_unauthorised_presenter_cannot_read_the_form_or_enter_generation()
    {
        var form = new RecordingForm();
        var workflow = new RecordingWorkflow();
        var context = Context(form);
        context.User = new ClaimsPrincipal();
        var gate = new ModelWorkGate();

        var failure = await Assert.ThrowsAsync<DomainException>(() => Generate(context, Access(context), workflow, gate));

        Assert.Equal("forbidden", failure.Code);
        Assert.Equal(0, form.Reads);
        Assert.Equal(0, workflow.Calls);
        AssertPermitReleased(gate);
    }

    private static DefaultHttpContext Context(RecordingForm form, CancellationToken aborted = default)
    {
        var context = new DefaultHttpContext
        {
            RequestAborted = aborted,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new("tid", TestData.Presenter.TenantId.ToString("D")),
                new("oid", TestData.Presenter.ObjectId.ToString("D"))
            ], "synthetic-handler-test"))
        };
        context.Features.Set<IFormFeature>(form);
        return context;
    }

    private static PresenterAccess Access(HttpContext context) => new(new HttpContextAccessor { HttpContext = context },
        new PresenterPolicy(TestData.Presenter.TenantId, new HashSet<Guid> { TestData.Presenter.ObjectId }, TestData.ClientId));

    private static void AssertPermitReleased(ModelWorkGate gate)
    {
        using var permit = gate.TryEnter(TestData.Presenter);
        Assert.NotNull(permit);
    }

    private static Dictionary<string, StringValues> Values() => new()
    {
        ["Topic"] = "Sorting algorithms",
        ["EducationalLevel"] = "GCSE (OCR J277)",
        ["DurationMinutes"] = "60",
        ["Instructions"] = ""
    };

    private static IFormCollection ValidForm() => new FormCollection(Values());

    private sealed class RecordingForm : IFormFeature
    {
        public Func<CancellationToken, Task<IFormCollection>> Read { get; set; } = _ => Task.FromResult(ValidForm());
        public int Reads { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool HasFormContentType => true;
        public IFormCollection? Form { get; set; }
        public IFormCollection ReadForm() => throw new NotSupportedException();
        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Token = cancellationToken;
            return Read(cancellationToken);
        }
    }

    private sealed class RecordingWorkflow : ILessonWorkflow
    {
        public LessonRecord Lesson { get; } = TestData.Lesson();
        public Func<CancellationToken, Task<LessonRecord>>? Run { get; set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public LessonRequest? Request { get; private set; }
        public PresenterIdentity? Presenter { get; private set; }
        public IProgress<ActivityEvent>? Progress { get; private set; }
        public Task<LessonRecord> GenerateAsync(LessonRequest request, PresenterIdentity presenter,
            IProgress<ActivityEvent>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Presenter = presenter;
            Progress = progress;
            Token = cancellationToken;
            return Run?.Invoke(cancellationToken) ?? Task.FromResult(Lesson);
        }
        public Task<QuizDraft> DraftQuizAsync(string lessonId, PresenterIdentity presenter,
            IProgress<ActivityEvent>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
