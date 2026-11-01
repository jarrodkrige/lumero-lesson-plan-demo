using LessonPlanner.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;

namespace LessonPlanner.Web.Security;

public static class WebSecurityRegistration
{
    public static IServiceCollection AddPresenterSecurity(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!Guid.TryParse(configuration["AzureAd:TenantId"], out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(configuration["AzureAd:ClientId"], out var client) || client == Guid.Empty ||
            !Guid.TryParse(configuration["Mcp:CallerClientId"], out var caller) || caller == Guid.Empty)
            throw new InvalidOperationException("Configure valid Entra tenant, web client and MCP caller identifiers.");
        var configuredPresenters = configuration.GetSection("Presenter:AllowedObjectIds").Get<string[]>() ?? [];
        var allowed = new HashSet<Guid>();
        foreach (var value in configuredPresenters)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new InvalidOperationException("Every presenter allowlist entry must be an Entra object identifier.");
            allowed.Add(id);
        }
        if (allowed.Count is < 1 or > 20) throw new InvalidOperationException("Configure between one and twenty authorised presenters.");
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        ValidateCredentials(configuration, local);
        if (configuration["AzureAd:Instance"] is not (null or "https://login.microsoftonline.com/"))
            throw new InvalidOperationException("This application requires the configured single-tenant Microsoft Entra authority.");
        var policy = new PresenterPolicy(tenant, allowed, caller.ToString("D"));
        services.AddSingleton(policy);
        services.AddHttpContextAccessor();
        services.AddScoped<PresenterAccess>();
        services.AddScoped<ParticipantTickets>();
        services.AddCascadingAuthenticationState();
        // Keep MSAL's byte-sized entries separate from unrelated consumers of IMemoryCache.
        services.AddKeyedSingleton<IMemoryCache>("PresenterTokens", (_, _) =>
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 8 * 1024 * 1024 }));
        services.AddSingleton<IMsalTokenCacheProvider>(provider => new MsalMemoryTokenCacheProvider(
            provider.GetRequiredKeyedService<IMemoryCache>("PresenterTokens"),
            provider.GetRequiredService<IOptions<MsalMemoryTokenCacheOptions>>()));
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches(options => options.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2));
        services.Configure<MicrosoftIdentityOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.MapInboundClaims = false;
            options.UseFastUnboundedCache = false;
        });
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.MapInboundClaims = false;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidIssuer = $"https://login.microsoftonline.com/{tenant:D}/v2.0";
            options.ProtocolValidator.RequireNonce = true;
            var tokenValidated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                // MSAL handles redemption, so ASP.NET skips its token-response protocol checks.
                if (context.TokenEndpointResponse is not null)
                    options.ProtocolValidator.ValidateTokenResponse(new()
                    {
                        ClientId = options.ClientId,
                        ProtocolMessage = context.TokenEndpointResponse,
                        ValidatedIdToken = context.SecurityToken,
                        Nonce = context.Nonce
                    });
                await tokenValidated(context);
            };
            options.Events.OnRemoteFailure = PresenterSignInFailure.HandleAsync;
        });
        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = local ? "Lumero.Presenter" : "__Host-Lumero.Presenter";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.SlidingExpiration = false;
            options.ExpireTimeSpan = TimeSpan.FromHours(2);
            options.AccessDeniedPath = "/access-denied";
            options.Events.OnValidatePrincipal = async context =>
            {
                if (context.Principal is not null && policy.Allows(context.Principal)) return;
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            };
        });
        services.AddAuthorization(options => options.AddPolicy("Presenter", builder =>
            builder.RequireAuthenticatedUser().RequireAssertion(context => policy.Allows(context.User))));
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = local ? "Lumero.Antiforgery" : "__Host-Lumero.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.FormFieldName = "__RequestVerificationToken";
        });
        var keys = services.AddDataProtection().SetApplicationName("Lumero.LessonPlanner.v1");
        if (local)
            keys.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(environment.ContentRootPath, ".local", "data-protection")));
        else
        {
            if (!Uri.TryCreate(configuration["DataProtection:BlobUri"], UriKind.Absolute, out var blob) ||
                blob.Scheme != "https" || !string.IsNullOrEmpty(blob.Query) || !string.IsNullOrEmpty(blob.Fragment) ||
                !string.IsNullOrEmpty(blob.UserInfo) || blob.AbsolutePath.Trim('/').Split('/').Length < 2)
                throw new InvalidOperationException("DataProtection:BlobUri must identify the private key-ring blob without a SAS token.");
            keys.PersistKeysToAzureBlobStorage(blob,
                InfrastructureRegistration.CreateCredential(configuration["DataProtection:ManagedIdentityClientId"], false));
        }
        return services;
    }

    public static void ValidateCredentials(IConfiguration configuration, bool local)
    {
        if (!string.IsNullOrWhiteSpace(configuration["AzureAd:ClientSecret"]) ||
            configuration.GetSection("AzureAd:ClientCertificates").GetChildren().Any())
            throw new InvalidOperationException("Configure one explicit ClientCredentials entry, without legacy secrets or certificate fallback.");
        var credentials = configuration.GetSection("AzureAd:ClientCredentials").GetChildren().ToArray();
        if (credentials.Length != 1)
            throw new InvalidOperationException("Configure one explicit Entra managed-identity assertion credential.");
        var credential = credentials[0];
        if (!string.IsNullOrWhiteSpace(credential["ClientSecret"]))
            throw new InvalidOperationException("Client secret credentials are not supported.");
        if (credential["SourceType"] == "SignedAssertionFromManagedIdentity")
        {
            if (credential["ManagedIdentityClientId"] is { Length: > 0 } id &&
                (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty))
                throw new InvalidOperationException("ManagedIdentityClientId must be an Entra client identifier.");
            return;
        }
        if (local && credential["SourceType"] == "Path" && !string.IsNullOrWhiteSpace(credential["CertificateDiskPath"])) return;
        throw new InvalidOperationException("Production requires a federated managed-identity assertion. A certificate path is permitted only for local development.");
    }
}
