using System.Threading.RateLimiting;
using LessonPlanner.Infrastructure;
using LessonPlanner.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;

namespace LessonPlanner.Mcp;

public static class McpApplication
{
    public const string PublicationPolicy = "PublicationCaller";

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        if (!Guid.TryParse(configuration["Mcp:TenantId"], out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(configuration["Mcp:CallerClientId"], out var callerId) || callerId == Guid.Empty)
            throw new InvalidOperationException("Mcp:TenantId and Mcp:CallerClientId must be configured Entra identifiers.");
        var audience = configuration["Mcp:Audience"];
        if (string.IsNullOrWhiteSpace(audience)) throw new InvalidOperationException("Mcp:Audience is required.");
        var role = configuration["Mcp:RequiredRole"] ?? "Quiz.Publish";
        var issuer = $"https://login.microsoftonline.com/{tenantId:D}/v2.0";
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16384);
        builder.AddServiceDefaults();
        builder.Services.AddLessonInfrastructure(configuration, builder.Environment);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new()
            {
                ValidateAudience = true, ValidAudience = audience,
                ValidateIssuer = true, ValidIssuer = issuer,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RoleClaimType = "roles", NameClaimType = "oid"
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy(PublicationPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireRole(role).RequireAssertion(context =>
                Guid.TryParse(context.User.FindFirst("tid")?.Value, out var tid) && tid == tenantId &&
                Guid.TryParse(context.User.FindFirst("azp")?.Value, out var azp) && azp == callerId &&
                context.User.FindFirst("scp") is null)));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("mcp", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("azp")?.Value ?? "unauthenticated", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddMcpServer().WithHttpTransport(options =>
            options.SessionMode = HttpServerSessionMode.Stateless).WithTools<QuizPublicationTool>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapDefaultEndpoints();
        app.MapMcp("/mcp").RequireAuthorization(PublicationPolicy).RequireRateLimiting("mcp");
        return app;
    }
}
