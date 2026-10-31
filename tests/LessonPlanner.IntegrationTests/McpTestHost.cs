using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Core;
using LessonPlanner.Infrastructure;
using LessonPlanner.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LessonPlanner.IntegrationTests;

internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly RSA rsa;
    private readonly WebApplication app;
    private readonly RsaSecurityKey key;
    private readonly Func<int> droppedResponseCount;
    public Uri Endpoint { get; }
    public bool ResponseWasDropped => droppedResponseCount() > 0;
    public static string Issuer => $"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/v2.0";

    private McpTestHost(WebApplication app, RSA rsa, RsaSecurityKey key, Uri endpoint, Func<int> droppedResponseCount)
    {
        this.rsa = rsa;
        this.app = app;
        this.key = key;
        this.droppedResponseCount = droppedResponseCount;
        Endpoint = endpoint;
    }

    public static async Task<McpTestHost> StartAsync(LocalTableFixture storage, string? dropFirstPublicationResponse = null)
    {
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { EnvironmentName = "Testing", ApplicationName = typeof(McpApplication).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:TenantId"] = TestData.Presenter.TenantId.ToString("D"),
            ["Mcp:CallerClientId"] = TestData.ClientId,
            ["Mcp:Audience"] = TestData.Audience,
            ["Mcp:RequiredRole"] = "Quiz.Publish",
            ["Quiz:PublicBaseUrl"] = storage.Policy.PublicBaseUrl.ToString(),
            ["Quiz:LifetimeHours"] = "1",
            ["Storage:ConnectionString"] = LocalTableFixture.Connection,
            ["Storage:TableName"] = storage.TableName
        });
        builder.Services.AddSingleton<TimeProvider>(storage.Clock);
        // Only this test host replaces the signing-key source. Production JWT policy still runs.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
        var app = McpApplication.Build(builder);
        var dropped = 0;
        if (dropFirstPublicationResponse is not null)
        {
            app.Use(async (context, next) =>
            {
                var original = context.Response.Body;
                using var buffered = new MemoryStream();
                context.Response.Body = buffered;
                try
                {
                    await next(context);
                    if (Volatile.Read(ref dropped) == 0 &&
                        await storage.Store().FindPublishedAsync(dropFirstPublicationResponse, CancellationToken.None) is not null &&
                        Interlocked.CompareExchange(ref dropped, 1, 0) == 0)
                    {
                        context.Abort();
                        return;
                    }
                    buffered.Position = 0;
                    await buffered.CopyToAsync(original, context.RequestAborted);
                }
                finally { context.Response.Body = original; }
            });
        }
        try
        {
            await app.StartAsync();
            return new(app, rsa, key, new Uri(new Uri(app.Urls.Single()), "/mcp"), () => Volatile.Read(ref dropped));
        }
        catch { await app.DisposeAsync(); rsa.Dispose(); throw; }
    }

    public string Token(string variation = "valid")
    {
        var claims = new List<Claim>
        {
            new("tid", variation == "wrong-tenant" ? Guid.NewGuid().ToString("D") : TestData.Presenter.TenantId.ToString("D")),
            new("azp", variation == "wrong-caller" ? Guid.NewGuid().ToString("D") : TestData.ClientId),
            new("idtyp", "app"), new("ver", "2.0")
        };
        if (variation != "missing-role") claims.Add(new("roles", "Quiz.Publish"));
        if (variation == "delegated") claims.Add(new("scp", "Quiz.Publish"));
        using var wrongRsa = RSA.Create(2048);
        var signingKey = variation == "wrong-key" ? new RsaSecurityKey(wrongRsa) { KeyId = key.KeyId } : key;
        var token = new JwtSecurityToken(variation == "wrong-issuer" ? "https://invalid.example/issuer" : Issuer,
            variation == "wrong-audience" ? "different-api" : TestData.Audience, claims,
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: DateTime.UtcNow.AddMinutes(variation == "expired" ? -5 : 5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public OfficialMcpPublicationClient Client(LocalTableFixture storage, string? token = null) => new(
        new(Endpoint, $"api://{TestData.Audience}/.default"), new LocalTestCredential(token is null ? () => Token() : () => token), storage.Policy);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        rsa.Dispose();
    }
}

internal sealed class LocalTestCredential(Func<string> token) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(token(), DateTimeOffset.UtcNow.AddMinutes(5));
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
