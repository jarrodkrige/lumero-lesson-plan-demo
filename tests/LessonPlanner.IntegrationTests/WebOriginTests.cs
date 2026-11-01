using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApplicationFactory = LessonPlanner.Web.WebApplicationFactory;

namespace LessonPlanner.IntegrationTests;

public sealed class WebOriginTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public async Task Production_forces_https_by_default_before_downstream_middleware(string? enabled)
    {
        await using var host = await FixedOriginTestHost.StartAsync(overrides: new Dictionary<string, string?>
        {
            ["ReverseProxy:UseFixedHttpsOrigin"] = enabled,
            ["ReverseProxy:KnownNetworks:0"] = "unused-invalid-cidr"
        });
        var result = await host.SendAsync();
        Assert.Equal(StatusCodes.Status204NoContent, result.Response.StatusCode);
        Assert.Equal("https", result.Response.Headers["X-Test-Scheme"]);
        Assert.Equal(FixedOriginTestHost.CanonicalHost, result.Response.Headers["X-Test-Host"]);
        Assert.Contains("max-age=", result.Response.Headers.StrictTransportSecurity.ToString(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(result.Response.Headers.Location));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task Production_ignores_spoofed_forwarding_headers_including_from_loopback(string scheme)
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        var result = await host.SendAsync(scheme: scheme, directIp: IPAddress.Loopback, spoofHeaders: true);
        Assert.Equal(StatusCodes.Status204NoContent, result.Response.StatusCode);
        Assert.Equal("https", result.Response.Headers["X-Test-Scheme"]);
        Assert.Equal(FixedOriginTestHost.CanonicalHost, result.Response.Headers["X-Test-Host"]);
        Assert.Equal(IPAddress.Loopback.ToString(), result.Response.Headers["X-Test-Remote-IP"]);
        Assert.True(string.IsNullOrEmpty(result.Response.Headers["X-Test-Path-Base"]));
    }

    [Theory]
    [InlineData("Development", "http")]
    [InlineData("Development", "https")]
    [InlineData("Testing", "http")]
    [InlineData("Testing", "https")]
    [InlineData("Staging", "https")]
    public async Task Non_production_preserves_the_direct_scheme_and_authority_even_with_the_flag(string environment, string scheme)
    {
        await using var host = await FixedOriginTestHost.StartAsync(environment, new Dictionary<string, string?>
        {
            ["ReverseProxy:UseFixedHttpsOrigin"] = "true",
            ["ReverseProxy:KnownNetworks:0"] = "unused-invalid-cidr",
            ["AllowedHosts"] = "*"
        });
        const string authority = "other.example.org:8443";
        var result = await host.SendAsync(scheme: scheme, authority: authority, spoofHeaders: true);
        Assert.Equal(StatusCodes.Status204NoContent, result.Response.StatusCode);
        Assert.Equal(scheme, result.Response.Headers["X-Test-Scheme"]);
        Assert.Equal(authority, result.Response.Headers["X-Test-Host"]);
        Assert.Equal(IPAddress.Loopback.ToString(), result.Response.Headers["X-Test-Remote-IP"]);
    }

    [Fact]
    public async Task Disabling_the_production_flag_keeps_host_validation_and_redirects_http_before_authentication()
    {
        await using var host = await FixedOriginTestHost.StartAsync(overrides: new Dictionary<string, string?>
        {
            ["ReverseProxy:UseFixedHttpsOrigin"] = "false"
        });
        var directHttp = await host.SendAsync(path: "/auth/sign-in", spoofHeaders: true);
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, directHttp.Response.StatusCode);
        Assert.Equal($"{FixedOriginTestHost.PublicOrigin}/auth/sign-in", directHttp.Response.Headers.Location);
        Assert.Equal(0, directHttp.Response.Headers.SetCookie.Count);
        var directHttps = await host.SendAsync(scheme: "https", spoofHeaders: true);
        Assert.Equal(StatusCodes.Status204NoContent, directHttps.Response.StatusCode);
        Assert.Equal("https", directHttps.Response.Headers["X-Test-Scheme"]);
        var wrongHost = await host.SendAsync(authority: "classroom.example.org:8443");
        Assert.Equal(StatusCodes.Status400BadRequest, wrongHost.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("other.example.org")]
    [InlineData("classroom.example.org.attacker.example.org")]
    [InlineData("classroom.example.org.")]
    [InlineData("classroom.example.org:80")]
    [InlineData("classroom.example.org:8443")]
    [InlineData("classroom.example.org:0443")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Production_rejects_noncanonical_host_before_oidc_and_without_health_probe_exceptions(string authority)
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        foreach (var path in new[] { "/auth/sign-in", "/signin-oidc", "/signout-callback-oidc", "/health/live" })
        {
            var result = await host.SendAsync(path: path, authority: authority, spoofHeaders: true,
                forwardedHost: FixedOriginTestHost.CanonicalHost);
            Assert.Equal(StatusCodes.Status400BadRequest, result.Response.StatusCode);
            Assert.True(string.IsNullOrEmpty(result.Response.Headers.Location));
            Assert.Equal(0, result.Response.Headers.SetCookie.Count);
        }
    }

    [Fact]
    public async Task Canonical_health_probe_is_accepted_without_an_exception()
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        var result = await host.SendAsync(path: "/health/live");
        Assert.Equal(StatusCodes.Status200OK, result.Response.StatusCode);
    }

    [Theory]
    [InlineData("classroom.example.org")]
    [InlineData("CLASSROOM.EXAMPLE.ORG")]
    [InlineData("classroom.example.org:443")]
    [InlineData("CLASSROOM.EXAMPLE.ORG:443")]
    public async Task Oidc_handler_constructs_exact_https_sign_in_and_sign_out_callbacks(string authority)
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        foreach (var scheme in new[] { "http", "https" })
        foreach (var spoofHeaders in new[] { false, true })
        {
            var signIn = await host.SendAsync(path: "/auth/sign-in", scheme: scheme, authority: authority, spoofHeaders: spoofHeaders);
            Assert.Equal(StatusCodes.Status302Found, signIn.Response.StatusCode);
            var authorize = new Uri(signIn.Response.Headers.Location.ToString(), UriKind.Absolute);
            Assert.Equal($"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/authorize", authorize.GetLeftPart(UriPartial.Path));
            var parameters = QueryHelpers.ParseQuery(authorize.Query);
            Assert.Equal("https://classroom.example.org/signin-oidc", Assert.Single(parameters["redirect_uri"]));
            Assert.Equal(FixedOriginTestHost.WebClientId, parameters["client_id"]);
            Assert.Equal("code", parameters["response_type"]);
            Assert.Equal("S256", parameters["code_challenge_method"]);
            Assert.False(string.IsNullOrEmpty(parameters["state"]));
            Assert.Equal(2, signIn.Response.Headers.SetCookie.Count);
            Assert.All(signIn.Response.Headers.SetCookie, cookie => Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));

            var signOut = await host.SendAsync(path: "/_test/oidc-sign-out", scheme: scheme, authority: authority, spoofHeaders: spoofHeaders);
            Assert.Equal(StatusCodes.Status302Found, signOut.Response.StatusCode);
            var endSession = new Uri(signOut.Response.Headers.Location.ToString(), UriKind.Absolute);
            Assert.Equal($"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/oauth2/v2.0/logout", endSession.GetLeftPart(UriPartial.Path));
            Assert.Equal("https://classroom.example.org/signout-callback-oidc",
                Assert.Single(QueryHelpers.ParseQuery(endSession.Query)["post_logout_redirect_uri"]));
        }
    }

    [Theory]
    [InlineData(null, "classroom.example.org")]
    [InlineData("", "classroom.example.org")]
    [InlineData("http://classroom.example.org", "classroom.example.org")]
    [InlineData("https://classroom.example.org:8443", "classroom.example.org")]
    [InlineData("https://classroom.example.org/path", "classroom.example.org")]
    [InlineData("https://classroom.example.org/path/..", "classroom.example.org")]
    [InlineData("https://classroom.example.org?query=value", "classroom.example.org")]
    [InlineData("https://classroom.example.org#fragment", "classroom.example.org")]
    [InlineData("https://user@classroom.example.org", "classroom.example.org")]
    [InlineData("https://127.0.0.1", "127.0.0.1")]
    [InlineData("https://[::1]", "[::1]")]
    [InlineData("https://localhost", "localhost")]
    [InlineData("https://classroom.example.org.", "classroom.example.org.")]
    [InlineData("https://class_room.example.org", "class_room.example.org")]
    [InlineData("https://classroom.example.org", null)]
    [InlineData("https://classroom.example.org", "")]
    [InlineData("https://classroom.example.org", "*")]
    [InlineData("https://classroom.example.org", "*.example.org")]
    [InlineData("https://classroom.example.org", "other.example.org")]
    [InlineData("https://classroom.example.org", "classroom.example.org:443")]
    [InlineData("https://classroom.example.org", "classroom.example.org;other.example.org")]
    [InlineData("https://classroom.example.org", "classroom.example.org;classroom.example.org")]
    [InlineData("https://classroom.example.org", " classroom.example.org ")]
    public void Production_configuration_fails_closed_even_when_scheme_forcing_is_disabled(string? origin, string? allowedHosts)
    {
        var builder = FixedOriginTestHost.CreateBuilder(overrides: new Dictionary<string, string?>
        {
            ["Quiz:PublicBaseUrl"] = origin,
            ["AllowedHosts"] = allowedHosts,
            ["ReverseProxy:UseFixedHttpsOrigin"] = "false"
        });
        Assert.Throws<InvalidOperationException>(() => WebApplicationFactory.AddApplicationServices(builder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-boolean")]
    public void Invalid_production_flag_fails_closed(string flag)
    {
        var builder = FixedOriginTestHost.CreateBuilder(overrides: new Dictionary<string, string?>
        {
            ["ReverseProxy:UseFixedHttpsOrigin"] = flag
        });
        Assert.Throws<InvalidOperationException>(() => WebApplicationFactory.AddApplicationServices(builder));
    }

    [Theory]
    [InlineData("https://classroom.example.org")]
    [InlineData("https://classroom.example.org/")]
    [InlineData("https://classroom.example.org:443/")]
    [InlineData("https://CLASSROOM.EXAMPLE.ORG")]
    public async Task Explicit_default_port_and_dns_case_are_normalized_to_one_origin(string origin)
    {
        await using var host = await FixedOriginTestHost.StartAsync(overrides: new Dictionary<string, string?>
        {
            ["Quiz:PublicBaseUrl"] = origin,
            ["AllowedHosts"] = "CLASSROOM.EXAMPLE.ORG"
        });
        var result = await host.SendAsync(authority: "CLASSROOM.EXAMPLE.ORG:443");
        Assert.Equal(StatusCodes.Status204NoContent, result.Response.StatusCode);
        Assert.Equal(FixedOriginTestHost.CanonicalHost, result.Response.Headers["X-Test-Host"]);
    }

    [Fact]
    public async Task Fixed_origin_does_not_weaken_production_cookie_or_oidc_options()
    {
        await using var host = await FixedOriginTestHost.StartAsync();
        var cookie = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal("__Host-Lumero.Presenter", cookie.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.False(cookie.SlidingExpiration);
        var oidc = host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.True(oidc.TokenValidationParameters.ValidateIssuer);
        Assert.Equal($"https://login.microsoftonline.com/{TestData.Presenter.TenantId:D}/v2.0", oidc.TokenValidationParameters.ValidIssuer);
        Assert.True(oidc.TokenValidationParameters.ValidateAudience);
        Assert.Equal(FixedOriginTestHost.WebClientId, oidc.ClientId);
        Assert.True(oidc.UsePkce);
        Assert.False(oidc.SaveTokens);
    }
}
