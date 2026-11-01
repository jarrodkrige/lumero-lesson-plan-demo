using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WebApplicationFactory = LessonPlanner.Web.WebApplicationFactory;

namespace LessonPlanner.IntegrationTests;

internal sealed class FixedOriginTestHost(WebApplication app, HttpClient backchannel) : IAsyncDisposable
{
    public const string CanonicalHost = "classroom.example.org";
    public const string PublicOrigin = "https://classroom.example.org";
    public const string WebClientId = "55555555-5555-4555-8555-555555555555";
    public IServiceProvider Services => app.Services;

    public static WebApplicationBuilder CreateBuilder(string environment = "Production",
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(WebApplicationFactory).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(root, "src", "LessonPlanner.Web")
        });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = CanonicalHost,
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = TestData.Presenter.TenantId.ToString("D"),
            ["AzureAd:ClientId"] = WebClientId,
            ["AzureAd:CallbackPath"] = "/signin-oidc",
            ["AzureAd:SignedOutCallbackPath"] = "/signout-callback-oidc",
            ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = TestData.ClientId,
            ["Presenter:AllowedObjectIds:0"] = TestData.Presenter.ObjectId.ToString("D"),
            ["Mcp:CallerClientId"] = TestData.ClientId,
            ["Mcp:Endpoint"] = "https://mcp.example.org/mcp",
            ["Mcp:TokenScope"] = $"api://{TestData.Audience}/.default",
            ["Mcp:ManagedIdentityClientId"] = TestData.ClientId,
            ["Storage:TableServiceUri"] = "https://storage.example.org",
            ["Storage:ManagedIdentityClientId"] = TestData.ClientId,
            ["DataProtection:BlobUri"] = "https://storage.example.org/private/keys.xml",
            ["DataProtection:ManagedIdentityClientId"] = TestData.ClientId,
            ["Foundry:Endpoint"] = "https://local-verification.openai.azure.com/openai/v1/",
            ["Foundry:Deployment"] = "synthetic-test-output-never-invoked",
            ["Foundry:ManagedIdentityClientId"] = TestData.ClientId,
            ["Quiz:PublicBaseUrl"] = PublicOrigin,
            ["Quiz:LifetimeHours"] = "1",
            ["Quiz:ApprovalMinutes"] = "5",
            ["Quiz:MaximumAttempts"] = "200"
        });
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
        builder.WebHost.UseTestServer();
        return builder;
    }

    public static async Task<FixedOriginTestHost> StartAsync(string environment = "Production",
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = CreateBuilder(environment, overrides);
        WebApplicationFactory.AddApplicationServices(builder);
        // Only key persistence and remote metadata are replaced. The actual OIDC handler and pipeline run.
        builder.Services.RemoveAll<IDataProtectionProvider>();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = new InMemoryKeyRepository());
        var backchannel = new HttpClient(new RejectNetworkHandler());
        builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Backchannel = backchannel;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new()
            {
                Issuer = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/v2.0",
                AuthorizationEndpoint = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/authorize",
                TokenEndpoint = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/token",
                EndSessionEndpoint = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/logout"
            });
        });
        WebApplication? app = null;
        try
        {
            app = WebApplicationFactory.ConfigurePipeline(builder.Build());
            app.MapGet("/_test/origin", (HttpContext context) =>
            {
                context.Response.Headers["X-Test-Scheme"] = context.Request.Scheme;
                context.Response.Headers["X-Test-Host"] = context.Request.Host.Value;
                context.Response.Headers["X-Test-Remote-IP"] = context.Connection.RemoteIpAddress?.ToString();
                context.Response.Headers["X-Test-Path-Base"] = context.Request.PathBase.Value;
                return Results.NoContent();
            }).AllowAnonymous().RequireRateLimiting("public");
            app.MapGet("/_test/oidc-sign-out", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous().RequireRateLimiting("public");
            await app.StartAsync();
            return new(app, backchannel);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            backchannel.Dispose();
            throw;
        }
    }

    public Task<HttpContext> SendAsync(string path = "/_test/origin", string scheme = "http",
        string authority = CanonicalHost, IPAddress? directIp = null, bool spoofHeaders = false, string? forwardedHost = null) =>
        app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString(authority);
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = directIp ?? IPAddress.Loopback;
            if (!spoofHeaders) return;
            context.Request.Headers["X-Forwarded-Proto"] = scheme == "http" ? "https" : "http";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.99, 203.0.113.44";
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost ?? "attacker.example.org:8443";
            context.Request.Headers["X-Forwarded-Prefix"] = "/spoofed";
            context.Request.Headers["Forwarded"] = "for=198.51.100.99;proto=http;host=attacker.example.org";
        }).WaitAsync(TimeSpan.FromSeconds(15));

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        backchannel.Dispose();
    }

    private sealed class InMemoryKeyRepository : IXmlRepository
    {
        private readonly ConcurrentQueue<XElement> elements = new();
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => elements.Enqueue(new XElement(element));
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("External authentication HTTP is forbidden in fixed-origin tests.");
    }
}
