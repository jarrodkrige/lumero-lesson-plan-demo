namespace LessonPlanner.Web.Security;

public sealed class FixedHttpsOrigin
{
    private readonly string canonicalHost;
    private readonly bool useFixedHttpsOrigin;

    private FixedHttpsOrigin(string canonicalHost, bool useFixedHttpsOrigin)
    {
        this.canonicalHost = canonicalHost;
        this.useFixedHttpsOrigin = useFixedHttpsOrigin;
    }

    public static FixedHttpsOrigin FromConfiguration(IConfiguration configuration)
    {
        var configuredOrigin = configuration["Quiz:PublicBaseUrl"];
        if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.HostNameType != UriHostNameType.Dns ||
            origin.IsLoopback || !IsDnsHost(origin.Host) ||
            !IsOrigin(configuredOrigin, origin.Host))
            throw new InvalidOperationException("Production Quiz:PublicBaseUrl must be one canonical HTTPS DNS origin on port 443, without a path, query, fragment or credentials.");
        if (!string.Equals(configuration["AllowedHosts"], origin.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production AllowedHosts must contain only the exact DNS host from Quiz:PublicBaseUrl, without a port or wildcard.");
        return new(origin.Host, configuration.GetValue("ReverseProxy:UseFixedHttpsOrigin", true));
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Only the Production pipeline installs this boundary; external ingress must enforce HTTPS.
        if (useFixedHttpsOrigin) context.Request.Scheme = Uri.UriSchemeHttps;
        var authority = context.Request.Host.Value;
        if (!string.Equals(authority, canonicalHost, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(authority, $"{canonicalHost}:443", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }
        // Strip the default port and DNS case variation so OIDC uses the exact configured origin.
        context.Request.Host = new HostString(canonicalHost);
        return next(context);
    }

    private static bool IsOrigin(string? value, string host) =>
        string.Equals(value, $"https://{host}", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, $"https://{host}/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, $"https://{host}:443", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, $"https://{host}:443/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDnsHost(string host) => host.Length <= 253 && host.Contains('.') &&
        host.Split('.').All(label => label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
}
