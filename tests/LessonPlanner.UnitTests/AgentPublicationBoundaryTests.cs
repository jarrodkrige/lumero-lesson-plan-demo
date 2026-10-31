using System.Text.Json;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using Microsoft.Extensions.AI;

namespace LessonPlanner.UnitTests;

public class AgentPublicationBoundaryTests
{
    [Fact]
    public async Task EmptyArgumentToolUsesOnlyCapturedApprovalAndCaller()
    {
        var approval = AgentTestData.Approval();
        var receipt = AgentTestData.Receipt();
        var client = new TestPublicationClient { Response = _ => Task.FromResult(receipt) };
        var tool = new ApprovedPublicationTool(client, approval, AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        var acknowledgement = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        Assert.Same(approval, client.Approval);
        Assert.Same(AgentTestData.Caller, client.Caller);
        Assert.Same(receipt, await tool.RequireReceiptAsync(CancellationToken.None));
        Assert.Equal(1, client.Calls);
        var result = JsonSerializer.Serialize(acknowledgement);
        Assert.Contains("true", result);
        Assert.DoesNotContain(approval.ApprovalId, result);
        Assert.DoesNotContain(approval.PayloadHash, result);
        Assert.False(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Empty(tool.JsonSchema.GetProperty("properties").EnumerateObject());
    }

    [Theory]
    [InlineData("approvalId")]
    [InlineData("draftId")]
    [InlineData("version")]
    [InlineData("payloadHash")]
    [InlineData("presenter")]
    [InlineData("callerClientId")]
    [InlineData("unexpected")]
    public async Task ModelSuppliedApprovalFieldsAreRejectedBeforeAnyMcpCall(string name)
    {
        var client = new TestPublicationClient();
        var tool = new ApprovedPublicationTool(client, AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<DomainException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments { [name] = "model-controlled" }, CancellationToken.None));
        Assert.Equal("publication_arguments", failure.Code);
        Assert.Equal(0, client.Calls);
        await Assert.ThrowsAsync<DomainException>(async () => await tool.RequireReceiptAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentToolRequestsReuseExactlyOneRealPublication()
    {
        var completion = new TaskCompletionSource<PublishedQuiz>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestPublicationClient { Response = _ => completion.Task };
        var tool = new ApprovedPublicationTool(client, AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        var invocations = Enumerable.Range(0, 16).Select(_ => tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask()).ToArray();
        Assert.Equal(1, client.Calls);
        var receipt = AgentTestData.Receipt();
        completion.SetResult(receipt);
        await Task.WhenAll(invocations);
        Assert.Same(receipt, await tool.RequireReceiptAsync(CancellationToken.None));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task FailedPublicationIsCachedAndNeverRetriedInsideTheTool()
    {
        var client = new TestPublicationClient { Response = _ => throw new DomainException("approval_expired", "Approval expired.") };
        var tool = new ApprovedPublicationTool(client, AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<DomainException>(async () => await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(async () => await tool.RequireReceiptAsync(CancellationToken.None));
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("id")]
    [InlineData("url")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task MissingOrInvalidToolReceiptsCannotBecomeSuccessfulPublications(string invalid)
    {
        var receipt = invalid switch
        {
            "null" => null!,
            "id" => AgentTestData.Receipt() with { Id = "not-a-server-id" },
            "url" => AgentTestData.Receipt() with { PublicUrl = new Uri("http://quiz.example.test/result") },
            "expired" => AgentTestData.Receipt() with { ExpiresAt = AgentTestData.Clock.GetUtcNow() },
            _ => AgentTestData.Receipt() with { PublishedAt = AgentTestData.Clock.GetUtcNow().AddHours(1) }
        };
        var client = new TestPublicationClient { Response = _ => Task.FromResult(receipt) };
        var tool = new ApprovedPublicationTool(client, AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<DomainException>(async () => await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        Assert.Equal("publication_receipt", failure.Code);
    }

    [Fact]
    public async Task CancelledToolCannotStartTheMcpOperation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new TestPublicationClient();
        var tool = new ApprovedPublicationTool(client, AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PublisherCannotTreatModelProseAsAMcpReceipt()
    {
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(AgentTestData.TextResponse("The quiz is published successfully.")) };
        var client = new TestPublicationClient();
        var failure = await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal("publication_not_invoked", failure.Code);
        Assert.Equal(0, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ActualToolReceiptWinsOverModelNarrationAndStopsTheAgentLoop()
    {
        var approval = AgentTestData.Approval();
        var receipt = AgentTestData.Receipt();
        var client = new TestPublicationClient { Response = _ => Task.FromResult(receipt) };
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(ToolResponse(
            new TextContent("Misleading model prose: publication was not attempted."), Call("one"))) };
        var result = await new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(approval, AgentTestData.Caller, CancellationToken.None);
        Assert.Same(receipt, result);
        Assert.Equal(1, client.Calls);
        Assert.Single(provider.Requests);
        Assert.Same(approval, client.Approval);
        var creation = Assert.Single(provider.Creations);
        Assert.Equal(AgentRole.QuizPublisher, creation.Role);
        Assert.Single(creation.Options.Tools!);
        Assert.Equal(256, creation.Options.MaxOutputTokens);
        Assert.False(creation.Options.AllowMultipleToolCalls);
        var input = string.Join(" ", Assert.Single(provider.Requests).Messages.Select(message => message.Text));
        Assert.DoesNotContain(approval.ApprovalId, input);
        Assert.DoesNotContain(approval.PayloadHash, input);
        Assert.DoesNotContain(AgentTestData.Caller.ClientId, input);
    }

    [Fact]
    public async Task MultipleFunctionCallsFailBeforeAnyMcpOperation()
    {
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(ToolResponse(Call("one"), Call("two"))) };
        var client = new TestPublicationClient();
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedKnownAndUnknownCallsCannotPartiallyPublish(bool unknownFirst)
    {
        var unknown = new FunctionCallContent("unknown", "alter_quiz", new Dictionary<string, object?>());
        var calls = unknownFirst ? new[] { unknown, Call("known") } : new[] { Call("known"), unknown };
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(ToolResponse(calls)) };
        var client = new TestPublicationClient();
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task UnknownToolCannotExecuteOrBeMistakenForPublication()
    {
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(ToolResponse(
            new FunctionCallContent("one", "approve_and_change_quiz", new Dictionary<string, object?>()))) };
        var client = new TestPublicationClient();
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task AgentCannotUseExtraArgumentsToAlterTheBoundDraft()
    {
        var provider = new ScriptedRoleProvider { Respond = (_, _) => Task.FromResult(ToolResponse(
            new FunctionCallContent("one", ApprovedPublicationTool.ToolName, new Dictionary<string, object?> { ["draftId"] = DomainRules.NewId() }))) };
        var client = new TestPublicationClient();
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ServerRefusalDoesNotTriggerAnotherInferenceOrPublicationAttempt()
    {
        var provider = CallingProvider();
        var client = new TestPublicationClient { Response = _ => throw new DomainException("approval_expired", "Approval expired.") };
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal(1, client.Calls);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task PublicationCallerCancellationPropagatesThroughTheRealFunctionPipeline()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new TestPublicationClient { Response = token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<PublishedQuiz>(token);
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QuizPublisherAgent(CallingProvider(), client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, cancellation.Token));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task PublicationDeadlineCancelsTheMcpOperationWithoutReportingSuccess()
    {
        var client = new TestPublicationClient { Response = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return AgentTestData.Receipt();
        } };
        var failure = await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(CallingProvider(), client,
                new() { ModelTimeout = TimeSpan.FromMilliseconds(30) }, AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval(), AgentTestData.Caller, CancellationToken.None));
        Assert.Equal("publication_timeout", failure.Code);
    }

    [Fact]
    public void OtherRolesCannotObtainThePublicationCapability()
    {
        var provider = new FoundryResponsesAgentProvider(new NoNetworkCredential(), new("https://resource.openai.azure.com", "test-model"), new());
        var tool = new ApprovedPublicationTool(new TestPublicationClient(), AgentTestData.Approval(), AgentTestData.Caller, AgentTestData.Clock, CancellationToken.None);
        Assert.Throws<DomainException>(() => provider.CreateAgent(AgentRole.Reviewer, "Test instructions.", new ChatOptions { Tools = [tool] }));
    }

    [Fact]
    public async Task InvalidBoundApprovalFailsBeforeCreatingAModelAgent()
    {
        var provider = CallingProvider();
        var client = new TestPublicationClient();
        await Assert.ThrowsAsync<DomainException>(() => new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .PublishAsync(AgentTestData.Approval() with { PayloadHash = "forged" }, AgentTestData.Caller, CancellationToken.None));
        Assert.Empty(provider.Creations);
        Assert.Equal(0, client.Calls);
    }

    private static ScriptedRoleProvider CallingProvider() => new() { Respond = (_, _) => Task.FromResult(ToolResponse(Call("one"))) };
    private static FunctionCallContent Call(string id) => new(id, ApprovedPublicationTool.ToolName, new Dictionary<string, object?>());
    private static ChatResponse ToolResponse(params AIContent[] contents) => new(new ChatMessage(ChatRole.Assistant, contents))
    {
        FinishReason = ChatFinishReason.ToolCalls
    };
}
