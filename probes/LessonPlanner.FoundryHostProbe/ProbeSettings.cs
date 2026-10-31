using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;

namespace LessonPlanner.FoundryHostProbe;

internal sealed record ProbeSettings(Uri ProjectEndpoint, string Deployment, string Credential, string IdentityId)
{
    public static ProbeSettings Read(Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        string Required(string name)
        {
            var value = readEnvironment($"Probe__{name}");
            return !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing probe configuration.");
        }

        var endpoint = Required("ProjectEndpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.DnsSafeHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(uri.AbsolutePath, @"\A/api/projects/[A-Za-z0-9][A-Za-z0-9._-]{0,127}/?\z"))
            throw new ArgumentException("An existing Foundry project HTTPS endpoint is required.");

        var deployment = Required("Deployment");
        if (!Regex.IsMatch(deployment, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z"))
            throw new ArgumentException("An explicit deployment name is required.");

        var credential = Required("Credential");
        var identity = credential switch
        {
            "AzureCli" => Required("TenantId"),
            "ManagedIdentity" => Required("ManagedIdentityClientId"),
            _ => throw new ArgumentException("Select one explicit Entra credential.")
        };
        if (!Guid.TryParseExact(identity, "D", out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("An explicit identity identifier is required.");
        return new(uri, deployment, credential, identity);
    }

    public TokenCredential CreateCredential() => Credential switch
    {
        "AzureCli" => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = IdentityId }),
        "ManagedIdentity" => new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(IdentityId)),
        _ => throw new ArgumentException("Unsupported credential.")
    };

}
