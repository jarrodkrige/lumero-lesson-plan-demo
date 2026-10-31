using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WebApplicationFactory = LessonPlanner.Web.WebApplicationFactory;

namespace LessonPlanner.IntegrationTests;

internal sealed class WebTestHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly McpTestHost mcp;
    private readonly LocalTableFixture storage;
    private readonly Uri previousOrigin;
    public Uri Origin { get; }
    public IServiceProvider Services => app.Services;
    public static readonly PresenterIdentity SecondPresenter = new(TestData.Presenter.TenantId,
        Guid.Parse("66666666-6666-4666-8666-666666666666"));

    private WebTestHost(WebApplication app, McpTestHost mcp, LocalTableFixture storage, Uri origin, Uri previousOrigin)
    {
        this.app = app;
        this.mcp = mcp;
        this.storage = storage;
        this.previousOrigin = previousOrigin;
        Origin = origin;
    }

    public static async Task<WebTestHost> StartAsync(LocalTableFixture storage, int? port = null,
        string? keyDirectory = null, Action<IServiceCollection>? configureTestServices = null)
    {
        var previous = storage.Policy.PublicBaseUrl;
        var origin = new Uri($"http://127.0.0.1:{port ?? AvailablePort()}");
        storage.UsePublicOrigin(origin);
        var mcp = await McpTestHost.StartAsync(storage);
        WebApplication? app = null;
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                ApplicationName = typeof(WebApplicationFactory).Assembly.GetName().Name,
                ContentRootPath = Path.Combine(root, "src", "LessonPlanner.Web")
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(origin.ToString()).UseStaticWebAssets();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:TenantId"] = TestData.Presenter.TenantId.ToString("D"),
                ["AzureAd:ClientId"] = "55555555-5555-4555-8555-555555555555",
                ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
                ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = TestData.ClientId,
                ["Presenter:AllowedObjectIds:0"] = TestData.Presenter.ObjectId.ToString("D"),
                ["Presenter:AllowedObjectIds:1"] = SecondPresenter.ObjectId.ToString("D"),
                ["Mcp:CallerClientId"] = TestData.ClientId,
                ["Mcp:Endpoint"] = mcp.Endpoint.ToString(),
                ["Mcp:TokenScope"] = $"api://{TestData.Audience}/.default",
                ["Storage:ConnectionString"] = LocalTableFixture.Connection,
                ["Storage:TableName"] = storage.TableName,
                ["Quiz:PublicBaseUrl"] = origin.ToString(),
                ["Quiz:LifetimeHours"] = "1",
                ["Quiz:ApprovalMinutes"] = "5",
                ["Quiz:MaximumAttempts"] = "200",
                ["Foundry:Endpoint"] = "https://local-verification.openai.azure.com/openai/v1/",
                ["Foundry:Deployment"] = "synthetic-test-output-never-invoked",
                ["Foundry:ManagedIdentityClientId"] = TestData.ClientId
            });
            WebApplicationFactory.AddApplicationServices(builder);
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(storage.Clock);
            LocalVerificationRoles.Register(builder.Services, mcp.Client(storage));
            builder.Services.AddDataProtection().SetApplicationName("Lumero.LessonPlanner.v1")
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory ?? Path.Combine(root, ".local", "test-keys", storage.TableName)));
            // Authentication replacements and the local sign-in endpoint exist only in this test assembly.
            builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new()
                {
                    Issuer = McpTestHost.Issuer,
                    AuthorizationEndpoint = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/authorize",
                    TokenEndpoint = $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/token"
                });
            });
            builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
                options => options.TimeProvider = storage.Clock);
            configureTestServices?.Invoke(builder.Services);
            app = WebApplicationFactory.ConfigurePipeline(builder.Build());
            app.MapGet("/_local/sign-in/{persona}", async (string persona, HttpContext context) =>
            {
                var presenter = persona == "second" ? SecondPresenter : TestData.Presenter;
                var objectId = persona == "denied" ? Guid.Parse("77777777-7777-4777-8777-777777777777") : presenter.ObjectId;
                var identity = new ClaimsIdentity([
                    new("tid", presenter.TenantId.ToString("D")), new("oid", objectId.ToString("D")),
                    new(ClaimTypes.NameIdentifier, objectId.ToString("D")), new(ClaimTypes.Name, "Local verification presenter")
                ], CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect("/presenter");
            });
            app.MapGet("/_local/notice", () => Results.Text(
                "LOCAL VERIFICATION ONLY. Model outputs and presenter identity are synthetic. MCP HTTP transport, JWT validation, Azure Table persistence, document rendering and server grading are real local implementations. No Entra sign-in or Foundry inference has been performed."));
            app.MapGet("/_local", () => Results.Content("""
                <!doctype html><html lang="en-GB"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Lumero local verification</title></head>
                <body><main><h1>Lumero local verification</h1><p>Model outputs and presenter identities in this harness are synthetic. No Entra sign-in or Foundry inference takes place.</p>
                <p>The application uses actual local Azure Table persistence, the official authenticated MCP HTTP transport, server-side grading, DOCX downloads and locally encoded QR images.</p>
                <p><a href="/_local/sign-in/presenter">Open the synthetic presenter session</a></p><p>This executable belongs to the test suite and must never be deployed.</p></main></body></html>
                """, "text/html; charset=utf-8"));
            app.MapPost("/_local/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
            {
                if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers["X-Lumero-Local-Control"] != "stop")
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                lifetime.StopApplication();
                return Results.NoContent();
            });
            await app.StartAsync();
            return new(app, mcp, storage, origin, previous);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            await mcp.DisposeAsync();
            storage.UsePublicOrigin(previous);
            throw;
        }
    }

    public HttpClient Client(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = cookies ?? new CookieContainer(),
        UseProxy = false
    }) { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(25) };

    public async Task SignInAsync(HttpClient client, string persona = "presenter")
    {
        using var response = await client.GetAsync($"/_local/sign-in/{persona}");
        response.EnsureSuccessOrRedirect();
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => app.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await mcp.DisposeAsync();
        storage.UsePublicOrigin(previousOrigin);
    }
}

internal static class TestResponseExtensions
{
    public static void EnsureSuccessOrRedirect(this HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 200 and < 400) return;
        response.EnsureSuccessStatusCode();
    }
}
