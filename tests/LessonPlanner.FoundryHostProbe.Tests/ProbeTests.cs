using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LessonPlanner.FoundryHostProbe.Tests;

public sealed class ProbeTests
{
    [Theory]
    [InlineData("AzureCli", "TenantId")]
    [InlineData("ManagedIdentity", "ManagedIdentityClientId")]
    public void Configuration_requires_one_explicit_credential(string credential, string identityKey)
    {
        var values = Valid();
        values["Probe__Credential"] = credential;
        values[$"Probe__{identityKey}"] = "10000000-0000-0000-0000-000000000001";
        var settings = ProbeSettings.Read(values.GetValueOrDefault);
        Assert.Equal(credential, settings.Credential);
        Assert.Equal(values[$"Probe__{identityKey}"], settings.IdentityId);
        Assert.Equal("model-deployment", settings.Deployment);
    }

    [Theory]
    [InlineData("http://example.services.ai.azure.com/api/projects/example")]
    [InlineData("https://example.services.ai.azure.com:8443/api/projects/example")]
    [InlineData("https://example.com/api/projects/example")]
    [InlineData("https://example.services.ai.azure.com.attacker.invalid/api/projects/example")]
    [InlineData("https://name@example.services.ai.azure.com/api/projects/example")]
    [InlineData("https://example.services.ai.azure.com/api/projects/example?api-version=test")]
    [InlineData("https://example.services.ai.azure.com/api/projects/example#fragment")]
    [InlineData("https://example.services.ai.azure.com/openai/v1/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/example%2Fother")]
    public void Untrusted_or_ambiguous_project_endpoints_are_rejected(string endpoint)
    {
        var values = Valid();
        values["Probe__ProjectEndpoint"] = endpoint;
        Assert.Throws<ArgumentException>(() => ProbeSettings.Read(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("ProjectEndpoint", "")]
    [InlineData("Deployment", "")]
    [InlineData("Deployment", "deployment name")]
    [InlineData("Deployment", "<deployment>")]
    [InlineData("Deployment", "deployment\n")]
    [InlineData("Credential", "")]
    [InlineData("Credential", "DefaultAzureCredential")]
    [InlineData("Credential", "ApiKey")]
    [InlineData("TenantId", "")]
    [InlineData("TenantId", "<tenant-id>")]
    [InlineData("TenantId", "00000000-0000-0000-0000-000000000000")]
    public void Missing_or_implicit_configuration_fails_closed(string key, string value)
    {
        var values = Valid();
        values[$"Probe__{key}"] = value;
        Assert.Throws<ArgumentException>(() => ProbeSettings.Read(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<managed-identity-client-id>")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Managed_identity_requires_an_explicit_client_identifier(string identity)
    {
        var values = Valid();
        values["Probe__Credential"] = "ManagedIdentity";
        values["Probe__ManagedIdentityClientId"] = identity;
        Assert.Throws<ArgumentException>(() => ProbeSettings.Read(values.GetValueOrDefault));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_adapter_applies_its_own_limits_without_mutating_caller_options(bool streaming)
    {
        var inner = new CapturingClient();
        using var client = new BoundedProbeClient(inner, "approved-deployment");
        var options = new ChatOptions
        {
            ModelId = "different-deployment",
            MaxOutputTokens = 9999,
            Tools = [AIFunctionFactory.Create(() => "synthetic test tool")]
        };
        ChatMessage[] messages = [new(ChatRole.User, "Synthetic local verification")];
        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages, options)) { }
        }
        else
        {
            await client.GetResponseAsync(messages, options);
        }
        Assert.NotNull(inner.Options);
        Assert.Equal("approved-deployment", inner.Options.ModelId);
        Assert.Equal(128, inner.Options.MaxOutputTokens);
        Assert.Empty(inner.Options.Tools!);
        Assert.Equal(ChatToolMode.None, inner.Options.ToolMode);
        Assert.Equal("different-deployment", options.ModelId);
        Assert.Equal(9999, options.MaxOutputTokens);
        Assert.Single(options.Tools);
    }

    [Fact]
    public async Task Model_adapter_also_limits_requests_without_options()
    {
        var inner = new CapturingClient();
        using var client = new BoundedProbeClient(inner, "approved-deployment");
        await client.GetResponseAsync([new(ChatRole.User, "Synthetic local verification")]);
        Assert.Equal(128, inner.Options?.MaxOutputTokens);
        Assert.Equal("approved-deployment", inner.Options?.ModelId);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Probe__ProjectEndpoint"] = "https://example.services.ai.azure.com/api/projects/example",
        ["Probe__Deployment"] = "model-deployment",
        ["Probe__Credential"] = "AzureCli",
        ["Probe__TenantId"] = "10000000-0000-0000-0000-000000000001"
    };

    private sealed class CapturingClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Synthetic verification")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            yield return new(ChatRole.Assistant, "Synthetic verification");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
