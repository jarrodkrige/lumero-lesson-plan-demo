using Azure.Core;
using LessonPlanner.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel.Primitives;

namespace LessonPlanner.Agents;

public interface IRoleAgentProvider
{
    AIAgent CreateAgent(AgentRole role, string instructions, ChatOptions options);
}

// This provider creates application-hosted agents, not registered Foundry service agents.
public sealed class FoundryResponsesAgentProvider : IRoleAgentProvider
{
    private readonly OpenAIClient _client;
    private readonly FoundryAgentOptions _settings;

    public FoundryResponsesAgentProvider(TokenCredential credential, FoundryAgentOptions settings, AgentExecutionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        ValidateDeployment(settings.DeploymentName);
        if (settings.RoleDeployments is null)
            throw new DomainException("agent_configuration", "Role deployment configuration is invalid.");
        foreach (var (role, deployment) in settings.RoleDeployments)
        {
            _ = AgentExecutionLimits.OutputTokens(role);
            ValidateDeployment(deployment);
        }
        _settings = settings;
#pragma warning disable OPENAI001 // The pinned stable OpenAI SDK marks its Entra authentication constructor experimental.
        _client = new OpenAIClient(new BearerTokenPolicy(credential, "https://ai.azure.com/.default"),
            new OpenAIClientOptions
            {
                Endpoint = NormaliseEndpoint(settings.Endpoint),
                NetworkTimeout = limits.ModelTimeout,
                RetryPolicy = new ClientRetryPolicy(0)
            });
#pragma warning restore OPENAI001
    }

    public AIAgent CreateAgent(AgentRole role, string instructions, ChatOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(options);
        _ = AgentExecutionLimits.OutputTokens(role);
        if (options.Tools is { Count: > 0 } &&
            (role != AgentRole.QuizPublisher || options.Tools.Count != 1 || options.Tools[0] is not ApprovedPublicationTool))
            throw new DomainException("agent_configuration", "Only the bound publication tool is permitted.");

        var chatOptions = options.Clone();
        chatOptions.Instructions = instructions;
        var deployment = _settings.RoleDeployments.GetValueOrDefault(role, _settings.DeploymentName);
#pragma warning disable OPENAI001 // Responses is required for this provider; keep the preview surface isolated here.
        return _client.GetResponsesClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = $"Lumero{role}",
            Description = $"Lumero {role} role, coordinated by the application.",
            ChatOptions = chatOptions,
            UseProvidedChatClientAsIs = true
        }, model: deployment, clientFactory: client => ConfigureClient(client, options.Tools is { Count: > 0 }));
#pragma warning restore OPENAI001
    }

    internal static IChatClient ConfigureClient(IChatClient client, bool publication)
    {
        if (!publication) return client;
        return new FunctionInvokingChatClient(client)
        {
            AllowConcurrentInvocation = false,
            IncludeDetailedErrors = false,
            MaximumIterationsPerRequest = 2,
            MaximumConsecutiveErrorsPerRequest = 0,
            TerminateOnUnknownCalls = true,
            FunctionInvoker = async (context, cancellationToken) =>
            {
                if (context.FunctionCount != 1 || context.Function is not ApprovedPublicationTool)
                    throw new DomainException("publication_tool", "Exactly one bound publication call is permitted.");
                context.Terminate = true;
                return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            }
        };
    }

    internal static Uri NormaliseEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback ||
            !(uri.DnsSafeHost.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
              uri.DnsSafeHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
              uri.DnsSafeHost.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath is not ("/" or "/openai/v1" or "/openai/v1/"))
            throw new DomainException("agent_configuration", "Configure an Azure Responses endpoint, not a Foundry project URL.");
        return new UriBuilder(uri) { Path = "/openai/v1/" }.Uri;
    }

    internal static void ValidateDeployment(string deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment) || deployment.Length > 100 ||
            deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new DomainException("agent_configuration", "Configure a valid model deployment name.");
    }
}
