using Azure.AI.Projects;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.AspNetCore.Http.Timeouts;

namespace LessonPlanner.FoundryHostProbe;

internal static class ProbeHost
{
    public static async Task RunAsync(ProbeSettings settings)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:8088");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
        builder.Logging.ClearProviders();
        builder.Services.AddRequestTimeouts(options =>
            options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) });

        var credential = settings.CreateCredential();
        var agent = new AIProjectClient(settings.ProjectEndpoint, credential)
            .AsAIAgent(
                model: settings.Deployment,
                instructions: "You are a synthetic connectivity probe. Reply with a short confirmation. Do not call tools or request personal information.",
                name: "lumero-hosting-capability-probe",
                clientFactory: client => new BoundedProbeClient(client, settings.Deployment));

#pragma warning disable MAAI001 // Only the isolated hosting experiment uses this preview surface.
        builder.AddFoundryResponses(agent, new FoundryAgentSessionStore(credential: credential), configure: options =>
        {
            options.AllowStoredOutputEnabled = false;
            options.ResilientBackground = false;
            options.SteerableConversations = false;
        });
#pragma warning restore MAAI001

        await using var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("The hosting capability probe did not complete.");
        }));
        app.UseRequestTimeouts();
        var acceptedRequests = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Origin") ||
                (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (context.Request.Path != "/responses" || Interlocked.Increment(ref acceptedRequests) != 1)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }
            await next(context);
        });
#pragma warning disable MAAI001
        app.MapFoundryResponses();
#pragma warning restore MAAI001
        app.MapGet("/probe-scope", () => Results.Text("Authorised single-request capability probe, not the workshop application."));
        Console.WriteLine("Authorised probe mode: one POST, a 16 KiB request limit, a 128-token model-adapter limit and a two-minute shutdown timer.");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await app.RunAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The probe has no indefinite serving mode.
        }
    }
}
