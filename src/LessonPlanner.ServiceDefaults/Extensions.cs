using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LessonPlanner.ServiceDefaults;

public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddServiceDiscovery();
        builder.Services.AddHealthChecks();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter("Lumero.LessonPlanner"))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = false;
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health") &&
                    !context.Request.Path.StartsWithSegments("/quiz") &&
                    !context.Request.Path.StartsWithSegments("/presenter") &&
                    !context.Request.Path.StartsWithSegments("/auth") &&
                    !context.Request.Path.StartsWithSegments("/signin-oidc") &&
                    !context.Request.Path.StartsWithSegments("/signout-callback-oidc");
            }).AddSource("Lumero.LessonPlanner"));
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        }).AllowAnonymous();
        return app;
    }
}
