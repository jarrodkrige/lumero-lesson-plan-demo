using System.Collections.Immutable;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

public sealed record FoundryAgentOptions(string Endpoint, string DeploymentName)
{
    public ImmutableDictionary<AgentRole, string> RoleDeployments { get; init; } = ImmutableDictionary<AgentRole, string>.Empty;
}

public sealed record AgentExecutionLimits
{
    public TimeSpan ModelTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan RetrievalTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan WorkflowTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumInputCharacters { get; init; } = 220_000;
    public int MaximumSourceCharacters { get; init; } = 160_000;
    public int MaximumOutputCharacters { get; init; } = 32_000;

    internal void Validate()
    {
        if (ModelTimeout <= TimeSpan.Zero || ModelTimeout > TimeSpan.FromMinutes(5) ||
            RetrievalTimeout <= TimeSpan.Zero || RetrievalTimeout > TimeSpan.FromMinutes(2) ||
            WorkflowTimeout <= TimeSpan.Zero || WorkflowTimeout > TimeSpan.FromMinutes(30) ||
            MaximumInputCharacters is < 1_024 or > 500_000 ||
            MaximumSourceCharacters is < 1_024 or > 200_000 ||
            MaximumOutputCharacters is < 1_024 or > 64_000)
            throw new DomainException("agent_configuration", "Agent execution budgets are invalid.");
    }

    internal static int OutputTokens(AgentRole role) => role switch
    {
        AgentRole.Orchestrator => 1_600,
        AgentRole.Curriculum => 3_600,
        AgentRole.LessonWriter => 7_000,
        AgentRole.Reviewer => 2_400,
        AgentRole.QuizPublisher => 5_500,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
