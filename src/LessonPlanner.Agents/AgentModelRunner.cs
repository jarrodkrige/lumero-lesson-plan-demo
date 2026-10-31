using Azure;
using Azure.Identity;
using LessonPlanner.Core;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Text.Json;

namespace LessonPlanner.Agents;

internal sealed class AgentModelRunner(IRoleAgentProvider provider, AgentExecutionLimits limits)
{
    internal async Task<T> RunAsync<T>(AgentRole role, string instructions, object input,
        int maximumOutputCharacters, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.Validate();
        var json = AgentJson.Input(input, limits.MaximumInputCharacters);
        var agent = provider.CreateAgent(role, AgentInstructions.Common + instructions, new ChatOptions
        {
            MaxOutputTokens = AgentExecutionLimits.OutputTokens(role),
            ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(AgentJson.Options),
            AllowMultipleToolCalls = false,
            ToolMode = ChatToolMode.None
        });
        var response = await AgentOperations.BoundedAsync(token => agent.RunAsync(
            new[] { new ChatMessage(ChatRole.User, json) }, cancellationToken: token),
            limits.ModelTimeout, "agent_timeout", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return AgentJson.Parse<T>(response.Text, Math.Min(maximumOutputCharacters, limits.MaximumOutputCharacters));
    }
}

internal static class AgentOperations
{
    internal static async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout,
        string timeoutCode, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await operation(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException(timeoutCode, "The operation did not complete within its time budget.");
        }
        catch (JsonException)
        {
            throw AgentJson.InvalidOutput();
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw new DomainException("agent_unavailable", "The required external service is unavailable. No replacement output was generated.");
        }
    }

    internal static bool IsTransportFailure(Exception exception) => exception is
        HttpRequestException or IOException or TimeoutException or ClientResultException or RequestFailedException or AuthenticationFailedException;
}
