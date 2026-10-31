using LessonPlanner.Core;
using Microsoft.Extensions.AI;

namespace LessonPlanner.Agents;

public sealed class QuizPublisherAgent(IRoleAgentProvider provider, IMcpPublicationClient publicationClient,
    AgentExecutionLimits limits, TimeProvider clock) : IQuizPublisherAgent
{
    public Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous, ReviewResult? feedback,
        CancellationToken cancellationToken)
    {
        AgentContractGuard.RequireSources(lesson.Sources, clock);
        if (feedback is not null) AgentContractGuard.RequireReview(feedback);
        return new AgentModelRunner(provider, limits).RunAsync<QuizContent>(AgentRole.QuizPublisher,
            AgentInstructions.Quiz, new
            {
                lesson = LessonBody.From(lesson),
                evidence = AgentContractGuard.Evidence(lesson.Sources),
                previous,
                feedback
            }, 24_000, cancellationToken);
    }

    public Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits.Validate();
        return AgentOperations.BoundedAsync(async token =>
        {
            var tool = new ApprovedPublicationTool(publicationClient, approval, caller, clock, token);
            var agent = provider.CreateAgent(AgentRole.QuizPublisher, AgentInstructions.Publish, new ChatOptions
            {
                Tools = [tool],
                ToolMode = ChatToolMode.RequireSpecific(ApprovedPublicationTool.ToolName),
                AllowMultipleToolCalls = false,
                MaxOutputTokens = 256
            });
            await agent.RunAsync(new[] { new ChatMessage(ChatRole.User,
                "Invoke the bound publication tool once with no arguments. Do not supply or infer approval fields.") },
                cancellationToken: token).ConfigureAwait(false);
            return await tool.RequireReceiptAsync(token).ConfigureAwait(false);
        }, limits.ModelTimeout, "publication_timeout", cancellationToken);
    }
}
