using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using ModelContextProtocol.Client;

namespace LessonPlanner.Agents;

public static class StackCompatibility
{
    public static IReadOnlyList<Type> ContractTypes { get; } = [typeof(AIAgent), typeof(Workflow), typeof(McpClient)];
}
