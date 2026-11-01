using System.Threading.RateLimiting;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using LessonPlanner.Infrastructure.Retrieval;
using LessonPlanner.Infrastructure.Rendering;
using LessonPlanner.ServiceDefaults;
using LessonPlanner.Web.Components;
using LessonPlanner.Web.Endpoints;
using LessonPlanner.Web.Security;
using LessonPlanner.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace LessonPlanner.Web;

public static class WebApplicationFactory
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        AddApplicationServices(builder);
        return ConfigurePipeline(builder.Build());
    }

    public static void AddApplicationServices(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        if (builder.Environment.IsProduction())
        {
            builder.Services.AddSingleton(FixedHttpsOrigin.FromConfiguration(configuration));
            builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
        }
        builder.AddServiceDefaults();
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 32_768);
        builder.Services.Configure<FormOptions>(options =>
        {
            options.ValueCountLimit = 32;
            options.KeyLengthLimit = 100;
            options.ValueLengthLimit = 4096;
            options.MultipartBodyLengthLimit = 32_768;
        });
        builder.Services.AddRazorComponents();
        builder.Services.AddPresenterSecurity(configuration, builder.Environment);
        builder.Services.AddLessonInfrastructure(configuration, builder.Environment);
        builder.Services.AddSingleton<ICurriculumRetriever, CuratedCurriculumRetriever>();
        builder.Services.AddSingleton<ILessonDocumentRenderer, OpenXmlLessonDocumentRenderer>();
        builder.Services.AddSingleton<IQuizQrRenderer>(_ => new LocalQuizQrRenderer(
            allowHttpLocalhost: builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")));
        builder.Services.AddLessonAgents(configuration, builder.Environment);
        builder.Services.AddSingleton<ModelWorkGate>();
        builder.Services.AddHttpClient<QuizLinkVerifier>(client => client.Timeout = TimeSpan.FromSeconds(6))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) });
        if (!Uri.TryCreate(configuration["Mcp:Endpoint"], UriKind.Absolute, out var mcpEndpoint))
            throw new InvalidOperationException("Mcp:Endpoint must identify the separate authenticated MCP service.");
        var mcpOptions = new McpConnectionOptions(mcpEndpoint, configuration["Mcp:TokenScope"] ?? "");
        mcpOptions.Validate(local);
        builder.Services.AddSingleton<IMcpPublicationClient>(services => new OfficialMcpPublicationClient(mcpOptions,
            InfrastructureRegistration.CreateCredential(configuration["Mcp:ManagedIdentityClientId"], local),
            services.GetRequiredService<QuizPolicy>()));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("application", _ => new ConcurrencyLimiterOptions
                { PermitLimit = 32, QueueLimit = 0 }));
            options.AddPolicy("public", new AnonymousRequestRateLimitPolicy());
            options.AddPolicy("model", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("oid")?.Value ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 6, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            options.OnRejected = async (rejection, cancellationToken) =>
            {
                var seconds = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
                    ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 1;
                rejection.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await rejection.HttpContext.Response.WriteAsync("The current request limit has been reached. Please wait before trying again.", cancellationToken);
            };
        });
    }

    public static WebApplication ConfigurePipeline(WebApplication app)
    {
        var local = app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing");
        if (app.Environment.IsProduction())
            app.Use(app.Services.GetRequiredService<FixedHttpsOrigin>().InvokeAsync);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'none'; style-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (Exception error)
            {
                app.Logger.LogError("A request failed with exception type {ErrorType}. No request contents were logged.", error.GetType().Name);
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("The service could not complete this request. No success has been assumed. Reload the page before trying again.");
            }
        });
        if (!local) { app.UseHsts(); app.UseHttpsRedirection(); }
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapDefaultEndpoints();
        app.MapStaticAssets();
        app.MapLessonEndpoints();
        app.MapRazorComponents<App>().RequireRateLimiting("public");
        return app;
    }
}
