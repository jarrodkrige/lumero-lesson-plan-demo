using Azure.Core;
using Azure.Identity;
using LessonPlanner.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LessonPlanner.Agents;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddLessonAgents(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var endpoint = configuration["Foundry:Endpoint"];
        var deployment = configuration["Foundry:Deployment"];
        if (endpoint is not { Length: > 0 and <= 2_048 } || deployment is not { Length: > 0 and <= 100 })
            throw new DomainException("agent_configuration", "Configure bounded Foundry endpoint and deployment settings.");
        _ = FoundryResponsesAgentProvider.NormaliseEndpoint(endpoint);
        FoundryResponsesAgentProvider.ValidateDeployment(deployment);

        var clientId = configuration["Foundry:ManagedIdentityClientId"];
        var identity = ManagedIdentityId.SystemAssigned;
        if (!string.IsNullOrEmpty(clientId))
        {
            if (clientId.Length != 36 || !Guid.TryParseExact(clientId, "D", out var parsedClientId) || parsedClientId == Guid.Empty)
                throw new DomainException("agent_configuration", "Configure a valid managed identity client ID or leave it empty.");
            identity = ManagedIdentityId.FromUserAssignedClientId(clientId);
        }

        TokenCredential credential = environment.IsDevelopment() || environment.IsEnvironment("Testing")
            ? new AzureCliCredential()
            : new ManagedIdentityCredential(identity);
        services.RemoveAll<TokenCredential>();
        services.AddSingleton(credential);
        return services.AddLessonPlannerAgents(new FoundryAgentOptions(endpoint, deployment));
    }

    public static IServiceCollection AddLessonPlannerAgents(this IServiceCollection services, FoundryAgentOptions settings,
        AgentExecutionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        limits ??= new AgentExecutionLimits();
        limits.Validate();
        _ = FoundryResponsesAgentProvider.NormaliseEndpoint(settings.Endpoint);
        services.TryAddSingleton(settings);
        services.TryAddSingleton(limits);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRoleAgentProvider, FoundryResponsesAgentProvider>();
        services.TryAddScoped<IOrchestratorAgent, OrchestratorAgent>();
        services.TryAddScoped<ICurriculumAgent, CurriculumAgent>();
        services.TryAddScoped<ILessonWriterAgent, LessonWriterAgent>();
        services.TryAddScoped<IReviewerAgent, ReviewerAgent>();
        services.TryAddScoped<IQuizPublisherAgent, QuizPublisherAgent>();
        services.TryAddScoped<ILessonWorkflow, LessonWorkflow>();
        return services;
    }
}
