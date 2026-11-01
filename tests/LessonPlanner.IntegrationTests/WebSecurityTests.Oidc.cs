using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace LessonPlanner.IntegrationTests;

public sealed partial class WebSecurityTests
{
    [Fact]
    public async Task Oidc_code_redemption_sends_managed_identity_assertion_and_pkce_without_saving_tokens_in_the_cookie()
    {
        using var provider = new SyntheticOidcProvider();
        await using var host = await FixedOriginTestHost.StartAsync(
            overrides: new Dictionary<string, string?> { ["AzureAd:UseFastUnboundedCache"] = "true" },
            configureServices: provider.Configure);
        using var client = host.CreateClient();
        var (response, challenge) = await RedeemCodeAsync(client, provider);
        using (response)
        {
            var request = Assert.Single(provider.TokenRequests);
            Assert.Equal(SyntheticOidcProvider.Assertion, request.GetValueOrDefault("client_assertion"));
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", request.GetValueOrDefault("client_assertion_type"));
            Assert.False(request.ContainsKey("client_secret"));
            Assert.Equal(1, provider.CredentialLoads);
            Assert.Equal("authorization_code", request["grant_type"]);
            Assert.Equal(SyntheticOidcProvider.AuthorizationCode, request["code"]);
            Assert.Equal(FixedOriginTestHost.WebClientId, request["client_id"]);
            Assert.Equal(FixedOriginTestHost.PublicOrigin + "/signin-oidc", request["redirect_uri"]);
            Assert.Equal(challenge["code_challenge"], WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request["code_verifier"]))));
            Assert.Equal("S256", challenge["code_challenge_method"]);
            Assert.Equal(new[] { "offline_access", "openid", "profile" }, challenge["scope"].Split(' ').Order().ToArray());
            Assert.Null(provider.Failure);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/presenter", response.Headers.Location?.OriginalString);
            Assert.True(provider.ValidatedPrincipal?.Identity?.IsAuthenticated);
            Assert.Equal(TestData.Presenter.TenantId.ToString("D"), provider.ValidatedPrincipal?.FindFirst("tid")?.Value);
            Assert.Equal(TestData.Presenter.ObjectId.ToString("D"), provider.ValidatedPrincipal?.FindFirst("oid")?.Value);

            using var presenterRequest = new HttpRequestMessage(HttpMethod.Get, "/_test/presenter");
            presenterRequest.Headers.Add("Cookie", Cookies(response));
            using var presenterResponse = await client.SendAsync(presenterRequest);
            Assert.Equal(HttpStatusCode.OK, presenterResponse.StatusCode);
            using var identity = JsonDocument.Parse(await presenterResponse.Content.ReadAsStringAsync());
            Assert.Equal(TestData.Presenter.TenantId.ToString("D"), identity.RootElement.GetProperty("tenant").GetString());
            Assert.Equal(TestData.Presenter.ObjectId.ToString("D"), identity.RootElement.GetProperty("objectId").GetString());
            Assert.Equal(0, identity.RootElement.GetProperty("savedTokens").GetInt32());

            var cache = Assert.IsType<MemoryCache>(host.Services.GetRequiredKeyedService<IMemoryCache>("PresenterTokens"));
            Assert.True(cache.Count > 0);

            using var signOut = new HttpRequestMessage(HttpMethod.Get, "/_test/oidc-sign-out");
            signOut.Headers.Add("Cookie", Cookies(response));
            using var signedOut = await client.SendAsync(signOut);
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            Assert.Equal(FixedOriginTestHost.PublicOrigin + "/signout-callback-oidc",
                QueryHelpers.ParseQuery(Assert.IsType<Uri>(signedOut.Headers.Location).Query)["post_logout_redirect_uri"]);
            Assert.Equal(0, cache.Count);
            Assert.Single(provider.TokenRequests);
        }
    }

    [Theory]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-nonce")]
    [InlineData("provider-error")]
    public async Task Oidc_rejects_invalid_token_or_provider_failure_without_granting_presenter_access(string variation)
    {
        using var provider = new SyntheticOidcProvider(variation);
        await using var host = await FixedOriginTestHost.StartAsync(configureServices: provider.Configure);
        using var client = host.CreateClient();
        var (response, _) = await RedeemCodeAsync(client, provider);
        using (response)
        {
            Assert.Single(provider.TokenRequests);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/sign-in-failed?reference=", response.Headers.Location?.OriginalString);
            Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                cookie => cookie.StartsWith("__Host-Lumero.Presenter=", StringComparison.Ordinal));
            using var page = await client.GetAsync(response.Headers.Location);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
            Assert.Contains("Presenter sign-in could not be completed", html);
            Assert.DoesNotContain("This account does not have presenter access", html);
            Assert.DoesNotContain(SyntheticOidcProvider.SensitiveError, html);
            Assert.DoesNotContain(SyntheticOidcProvider.Assertion, html);
        }
    }

    [Theory]
    [InlineData("missing-correlation", 0)]
    [InlineData("tampered-state", 0)]
    [InlineData("missing-nonce-cookie", 1)]
    public async Task Oidc_rejects_callback_without_its_original_state_and_cookies(string variation, int redemptions)
    {
        using var provider = new SyntheticOidcProvider();
        await using var host = await FixedOriginTestHost.StartAsync(configureServices: provider.Configure);
        using var client = host.CreateClient();
        var (response, _) = await RedeemCodeAsync(client, provider, variation);
        using (response)
        {
            Assert.NotNull(provider.Failure);
            Assert.Equal(redemptions, provider.TokenRequests.Count);
            Assert.Equal(redemptions, provider.CredentialLoads);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/sign-in-failed?reference=", response.Headers.Location?.OriginalString);
            Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                cookie => cookie.StartsWith("__Host-Lumero.Presenter=", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("22222222-2222-4222-8222-222222222222", "22222222-2222-4222-8222-222222222222")]
    [InlineData("untrusted\r\nlog-content", null)]
    public async Task Oidc_remote_failure_logs_only_safe_reference_and_valid_provider_correlation(string providerCorrelation, string? expectedCorrelation)
    {
        using var logs = new SignInLogCapture();
        await using var host = await FixedOriginTestHost.StartAsync(configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var failure = new InvalidOperationException("outer-" + SyntheticOidcProvider.SensitiveError,
            new MsalServiceException("invalid_client", SyntheticOidcProvider.SensitiveError) { CorrelationId = providerCorrelation });
        var context = new RemoteFailureContext(http,
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)), options, failure);
        await options.Events.RemoteFailure(context);
        Assert.True(context.Result?.Handled);
        var location = http.Response.Headers.Location.ToString();
        Assert.StartsWith("/sign-in-failed?reference=", location);
        var reference = QueryHelpers.ParseQuery(new Uri(new Uri(FixedOriginTestHost.PublicOrigin), location).Query)["reference"].ToString();
        Assert.True(Guid.TryParseExact(reference, "N", out _));
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("PresenterSignInFailed", log.Event.Name);
        Assert.Equal(reference, log.Values["Reference"]);
        Assert.Equal(expectedCorrelation, log.Values["ProviderCorrelationId"]);
        Assert.Null(log.Exception);
        Assert.DoesNotContain(SyntheticOidcProvider.SensitiveError, log.Message);
        Assert.DoesNotContain("untrusted", log.Message);

        using var client = host.CreateClient();
        using var page = await client.GetAsync(location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Contains(reference, html);
        Assert.DoesNotContain(SyntheticOidcProvider.SensitiveError, html);
        Assert.DoesNotContain(SyntheticOidcProvider.ProviderCorrelationId, html);
        using var denied = await client.GetAsync("/access-denied");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("This account does not have presenter access", await denied.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("invalid-reference")]
    [InlineData("")]
    public async Task Sign_in_failure_page_does_not_render_unvalidated_query_values(string reference)
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/sign-in-failed?reference=" + Uri.EscapeDataString(reference) + "&error_description=" + SyntheticOidcProvider.SensitiveError);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Support reference:", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("invalid-reference", html);
        Assert.DoesNotContain(SyntheticOidcProvider.SensitiveError, html);
    }

    [Fact]
    public async Task Presenter_token_cache_is_byte_bounded_isolated_and_expires_with_the_session()
    {
        await using var host = await FixedOriginTestHost.StartAsync(overrides: new Dictionary<string, string?>
        {
            ["AzureAd:UseFastUnboundedCache"] = "true"
        });
        var services = host.Services;
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(oidc.MapInboundClaims);
        Assert.False(Assert.IsType<Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler>(oidc.TokenHandler).MapInboundClaims);
        Assert.True(oidc.ProtocolValidator.RequireNonce);
        var identity = services.GetRequiredService<IOptionsMonitor<MicrosoftIdentityOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(identity.UseFastUnboundedCache);
        Assert.Equal(TimeSpan.FromHours(2), services.GetRequiredService<IOptions<MsalMemoryTokenCacheOptions>>().Value.AbsoluteExpirationRelativeToNow);
        var cache = services.GetRequiredKeyedService<IMemoryCache>("PresenterTokens");
        var shared = services.GetRequiredService<IMemoryCache>();
        Assert.NotSame(shared, cache);
        cache.Set("too-large", new byte[8 * 1024 * 1024 + 1], new MemoryCacheEntryOptions { Size = 8 * 1024 * 1024 + 1 });
        Assert.False(cache.TryGetValue("too-large", out _));
        cache.Set("small", new byte[4], new MemoryCacheEntryOptions { Size = 4 });
        Assert.True(cache.TryGetValue("small", out _));
        shared.Set("unrelated-cache-consumer", "no-token-data");
        Assert.Equal("no-token-data", shared.Get<string>("unrelated-cache-consumer"));
    }

    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Challenge)> RedeemCodeAsync(
        HttpClient client, SyntheticOidcProvider provider, string variation = "valid")
    {
        using var challenge = await client.GetAsync("/auth/sign-in");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var uri = Assert.IsType<Uri>(challenge.Headers.Location);
        var parameters = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => Assert.Single(pair.Value)!);
        provider.Nonce = parameters["nonce"];
        var cookies = Cookies(challenge);
        var excludedPrefix = variation switch
        {
            "missing-correlation" => ".AspNetCore.Correlation.",
            "missing-nonce-cookie" => ".AspNetCore.OpenIdConnect.Nonce.",
            _ => null
        };
        if (excludedPrefix is not null)
        {
            var entries = cookies.Split("; ");
            Assert.Contains(entries, entry => entry.StartsWith(excludedPrefix, StringComparison.Ordinal));
            cookies = string.Join("; ", entries.Where(entry => !entry.StartsWith(excludedPrefix, StringComparison.Ordinal)));
        }
        using var callback = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = SyntheticOidcProvider.AuthorizationCode,
                ["state"] = variation == "tampered-state" ? "invalid-" + parameters["state"] : parameters["state"]
            })
        };
        callback.Headers.Add("Cookie", cookies);
        return (await client.SendAsync(callback), parameters);
    }

    private static string Cookies(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));

    private sealed record SignInLog(LogLevel Level, EventId Event, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Values);

    private sealed class SignInLogCapture : ILoggerProvider
    {
        public ConcurrentQueue<SignInLog> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<SignInLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => category == "LessonPlanner.Web.Security.PresenterSignIn";
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary();
                entries.Enqueue(new(level, eventId, formatter(state, exception), exception, values));
            }
        }
    }
}
