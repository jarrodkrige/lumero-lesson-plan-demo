using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LessonPlanner.IntegrationTests;

// Test-only identity provider: every HTTP request and credential load stays in this process.
internal sealed class SyntheticOidcProvider(string variation = "valid") : IDisposable, IMsalHttpClientFactory, ICredentialSourceLoader
{
    public const string AuthorizationCode = "synthetic-authorization-code-never-valid-in-entra";
    public const string Assertion = "synthetic-managed-identity-assertion-never-valid-in-entra";
    public const string SensitiveError = "synthetic-private-provider-detail-do-not-display";
    public const string ProviderCorrelationId = "22222222-2222-4222-8222-222222222222";
    private const string AuthorityHost = "login.microsoftonline.com";
    private readonly RSA signingKey = RSA.Create(2048);
    private HttpClient? client;
    public string? Nonce { get; set; }
    public List<IReadOnlyDictionary<string, string>> TokenRequests { get; } = [];
    public int CredentialLoads { get; private set; }
    public Exception? Failure { get; private set; }
    public System.Security.Claims.ClaimsPrincipal? ValidatedPrincipal { get; private set; }
    public CredentialSource CredentialSource => CredentialSource.SignedAssertionFromManagedIdentity;
    private static string Tenant => TestData.Presenter.TenantId.ToString("D");
    private static string Issuer => $"https://{AuthorityHost}/{Tenant}/v2.0";
    private static string TokenEndpoint => $"https://{AuthorityHost}/{Tenant}/oauth2/v2.0/token";
    private SecurityKey Key => new RsaSecurityKey(signingKey) { KeyId = "synthetic-test-signing-key" };

    public void Configure(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IMsalHttpClientFactory>(this));
        services.Replace(ServiceDescriptor.Singleton<ICredentialsLoader>(new DefaultCredentialsLoader([this], null)));
        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Backchannel = GetHttpClient();
            var remoteFailure = options.Events.OnRemoteFailure;
            options.Events.OnRemoteFailure = context =>
            {
                Failure = context.Failure;
                return remoteFailure(context);
            };
            var tokenValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                await tokenValidated(context);
                ValidatedPrincipal = context.Principal;
            };
            var metadata = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = $"https://{AuthorityHost}/{Tenant}/oauth2/v2.0/authorize",
                TokenEndpoint = TokenEndpoint,
                EndSessionEndpoint = $"https://{AuthorityHost}/{Tenant}/oauth2/v2.0/logout"
            };
            metadata.SigningKeys.Add(Key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
    }

    public HttpClient GetHttpClient() => client ??= new HttpClient(new Handler(this)) { Timeout = TimeSpan.FromSeconds(5) };

    public Task LoadIfNeededAsync(CredentialDescription credential, CredentialSourceLoaderParameters? parameters)
    {
        Assert.Equal(CredentialSource.SignedAssertionFromManagedIdentity, credential.SourceType);
        Assert.Equal(TestData.ClientId, credential.ManagedIdentityClientId);
        Assert.Equal("api://AzureADTokenExchange/.default", credential.TokenExchangeUrl);
        Assert.Equal(CredentialType.SignedAssertion, credential.CredentialType);
        CredentialLoads++;
        credential.CachedValue = new SyntheticAssertionProvider();
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = Assert.IsType<Uri>(request.RequestUri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal(AuthorityHost, uri.Host);
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/common/discovery/instance")
            return Json(new
            {
                tenant_discovery_endpoint = $"https://{AuthorityHost}/{Tenant}/v2.0/.well-known/openid-configuration",
                metadata = new[] { new { preferred_network = AuthorityHost, preferred_cache = AuthorityHost, aliases = new[] { AuthorityHost } } }
            });
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == $"/{Tenant}/v2.0/.well-known/openid-configuration")
            return Json(new
            {
                issuer = Issuer,
                authorization_endpoint = $"https://{AuthorityHost}/{Tenant}/oauth2/v2.0/authorize",
                token_endpoint = TokenEndpoint,
                jwks_uri = $"https://{AuthorityHost}/{Tenant}/discovery/v2.0/keys"
            });
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TokenEndpoint, uri.AbsoluteUri);
        Assert.NotNull(request.Content);
        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        Assert.InRange(body.Length, 1, 16_384);
        var form = QueryHelpers.ParseQuery(body).ToDictionary(pair => pair.Key, pair => Assert.Single(pair.Value)!);
        TokenRequests.Add(form);
        if (form.GetValueOrDefault("client_assertion") != Assertion)
            return Json(new { error = "invalid_client", error_description = SensitiveError }, HttpStatusCode.BadRequest);
        if (variation == "provider-error")
            return Json(new { error = "invalid_grant", error_description = SensitiveError, correlation_id = ProviderCorrelationId }, HttpStatusCode.BadRequest);
        return Json(new
        {
            token_type = "Bearer",
            expires_in = 3600,
            scope = form.GetValueOrDefault("scope", "openid profile offline_access"),
            access_token = "synthetic-access-token-never-valid-in-entra",
            refresh_token = "synthetic-refresh-token-never-valid-in-entra",
            id_token = CreateIdToken(),
            client_info = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                uid = TestData.Presenter.ObjectId.ToString("D"),
                utid = Tenant
            }))
        });
    }

    private string CreateIdToken()
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = variation == "wrong-issuer" ? "https://issuer.example.org/untrusted" : Issuer,
            Audience = variation == "wrong-audience" ? "another-application" : FixedOriginTestHost.WebClientId,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "synthetic-presenter-subject",
                ["tid"] = Tenant,
                ["oid"] = TestData.Presenter.ObjectId.ToString("D"),
                ["nonce"] = variation == "wrong-nonce" ? "untrusted-nonce" : Assert.IsType<string>(Nonce)
            }
        });
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        client?.Dispose();
        signingKey.Dispose();
    }

    private sealed class Handler(SyntheticOidcProvider owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            owner.SendAsync(request, cancellationToken);
    }

    private sealed class SyntheticAssertionProvider : ClientAssertionProviderBase
    {
        protected override Task<ClientAssertion> GetClientAssertionAsync(AssertionRequestOptions? options) =>
            Task.FromResult(new ClientAssertion(Assertion, DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
