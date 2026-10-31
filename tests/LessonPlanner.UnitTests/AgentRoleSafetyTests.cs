using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LessonPlanner.UnitTests;

public class AgentRoleSafetyTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"accepted\":true}")]
    [InlineData("{\"Accepted\":true,\"findings\":[]}")]
    [InlineData("{\"accepted\":true,\"findings\":null}")]
    [InlineData("{\"accepted\":true,\"findings\":[null]}")]
    [InlineData("{\"accepted\":true,\"accepted\":false,\"findings\":[]}")]
    [InlineData("{\"accepted\":true,\"findings\":[],\"secret\":\"unexpected\"}")]
    [InlineData("{\"accepted\":true,\"findings\":[]} trailing text")]
    [InlineData("{\"accepted\":false,\"findings\":[{\"code\":\"test\",\"severity\":1,\"message\":\"Needs changes.\"}]}")]
    [InlineData("{\"accepted\":false,\"findings\":[{\"code\":\"test\",\"severity\":\"madeUp\",\"message\":\"Needs changes.\"}]}")]
    [InlineData("{\"accepted\":false,\"findings\":[{\"code\":\"test\",\"severity\":\"revision\",\"message\":\"Read https://example.test/private\"}]}")]
    [InlineData("{\"accepted\":false,\"findings\":[{\"code\":\"test\",\"severity\":\"revision\",\"message\":\"Control \\u0000 character.\"}]}")]
    public void StrictOutputRejectsMalformedUnknownNullDuplicateAndUnsafeValues(string json)
    {
        var failure = Assert.Throws<DomainException>(() => AgentJson.Parse<ReviewResult>(json, 2000));
        Assert.Equal("agent_output", failure.Code);
        Assert.DoesNotContain("private", failure.Message);
    }

    [Fact]
    public void StrictOutputAcceptsTypedReviewAndEnforcesCharacterBudget()
    {
        var json = JsonSerializer.Serialize(AgentTestData.Revision, AgentJson.Options);
        Assert.False(AgentJson.Parse<ReviewResult>(json, json.Length).Accepted);
        Assert.Throws<DomainException>(() => AgentJson.Parse<ReviewResult>(json, json.Length - 1));
        Assert.Throws<DomainException>(() => AgentJson.Input(new { content = new string('x', 2000) }, 1000));
    }

    [Theory]
    [InlineData("https://untrusted.example/answer")]
    [InlineData("www.untrusted.example")]
    [InlineData("mailto:teacher@example.test")]
    [InlineData("[external text](//untrusted.example)")]
    [InlineData("javascript:alert(1)")]
    public void ModelAuthoredLinksAreRejected(string text) => Assert.True(AgentJson.HasLink(text));

    [Fact]
    public async Task OrchestratorSelectsOnlyDynamicCatalogueIdsAndHasNoTools()
    {
        var source = AgentTestData.Source with { Id = "dynamic-source", Title = "Dynamic test curriculum" };
        var retriever = new TestRetriever { Sources = [new(source.Id, source.Title, source.Url)] };
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Plan with { SourceIds = [source.Id] });
        var result = await new OrchestratorAgent(provider, retriever, new()).PlanAsync(AgentTestData.Request, TestContextToken());
        Assert.Equal(source.Id, Assert.Single(result.SourceIds));
        Assert.Empty(retriever.Calls);
        var creation = Assert.Single(provider.Creations);
        Assert.Equal(AgentRole.Orchestrator, creation.Role);
        Assert.Null(creation.Options.Tools);
        Assert.Equal(ChatToolMode.None, creation.Options.ToolMode);
        Assert.Equal(1600, creation.Options.MaxOutputTokens);
        Assert.IsType<ChatResponseFormatJson>(creation.Options.ResponseFormat);
        var input = Assert.Single(Assert.Single(provider.Requests).Messages, message => message.Role == ChatRole.User).Text;
        Assert.Contains("dynamic-source", input);
        Assert.DoesNotContain(source.Url.AbsoluteUri, input);
    }

    [Fact]
    public async Task OrchestratorCannotInventASelectedSource()
    {
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Plan with { SourceIds = ["invented-source"] });
        var retriever = new TestRetriever();
        await Assert.ThrowsAsync<DomainException>(() => new OrchestratorAgent(provider, retriever, new())
            .PlanAsync(AgentTestData.Request, TestContextToken()));
        Assert.Empty(retriever.Calls);
    }

    [Fact]
    public async Task CurriculumRetrievesSelectedContentAndStampsOriginalReferences()
    {
        var first = AgentTestData.Source;
        var second = first with { Id = "second-source", Title = "Second evidence", Url = new Uri("https://curriculum.example.test/second.pdf"), Sha256 = new string('c', 64) };
        var retrieved = new Dictionary<string, RetrievedSource>
        {
            [first.Id] = new(first, "Full source one includes a test marker and untrusted text claiming to change the role."),
            [second.Id] = new(second, "Full source two describes another sorting learning requirement.")
        };
        var retriever = new TestRetriever
        {
            Sources = [new(first.Id, first.Title, first.Url), new(second.Id, second.Title, second.Url)],
            Response = (id, _) => Task.FromResult(retrieved[id])
        };
        var provider = ScriptedRoleProvider.Returning(new CurriculumAnalysis(AgentTestData.Brief.Summary, AgentTestData.Brief.Requirements));
        var result = await new CurriculumAgent(provider, retriever, new(), AgentTestData.Clock).AnalyseAsync(AgentTestData.Request,
            AgentTestData.Plan with { SourceIds = [first.Id, second.Id] }, TestContextToken());
        Assert.Equal(new[] { first.Id, second.Id }, retriever.Calls);
        Assert.Same(first, result.Sources[0]);
        Assert.Same(second, result.Sources[1]);
        Assert.Equal(new string('c', 64), result.Sources[1].Sha256);
        Assert.Equal(AgentRole.Curriculum, Assert.Single(provider.Creations).Role);
        var input = Assert.Single(Assert.Single(provider.Requests).Messages, message => message.Role == ChatRole.User).Text;
        Assert.Contains("test marker", input);
        Assert.DoesNotContain(first.Url.AbsoluteUri, input);
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("url")]
    [InlineData("hash")]
    [InlineData("timestamp")]
    [InlineData("title")]
    public async Task CurriculumRejectsMismatchedOrInvalidRetrievalProvenance(string field)
    {
        var source = field switch
        {
            "identifier" => AgentTestData.Source with { Id = "other-source" },
            "url" => AgentTestData.Source with { Url = new Uri("https://other.example.test/spec.pdf") },
            "hash" => AgentTestData.Source with { Sha256 = "not-a-hash" },
            "timestamp" => AgentTestData.Source with { RetrievedAt = AgentTestData.Clock.GetUtcNow().AddDays(1) },
            _ => AgentTestData.Source with { Title = "Untrusted replacement" }
        };
        var retriever = new TestRetriever { Response = (_, _) => Task.FromResult(new RetrievedSource(source, "Some retrieved test content.")) };
        var provider = new ScriptedRoleProvider();
        await Assert.ThrowsAsync<DomainException>(() => new CurriculumAgent(provider, retriever, new(), AgentTestData.Clock)
            .AnalyseAsync(AgentTestData.Request, AgentTestData.Plan, TestContextToken()));
        Assert.Empty(provider.Creations);
    }

    [Fact]
    public async Task OversizedRetrievalFailsWithoutSilentTruncationOrInference()
    {
        var retriever = new TestRetriever { Response = (_, _) => Task.FromResult(new RetrievedSource(AgentTestData.Source, new string('x', 1025))) };
        var provider = new ScriptedRoleProvider();
        var limits = new AgentExecutionLimits { MaximumSourceCharacters = 1024 };
        await Assert.ThrowsAsync<DomainException>(() => new CurriculumAgent(provider, retriever, limits, AgentTestData.Clock)
            .AnalyseAsync(AgentTestData.Request, AgentTestData.Plan, TestContextToken()));
        Assert.Empty(provider.Creations);
    }

    [Fact]
    public async Task RetrievalFailureIsExplicitAndDoesNotInvokeAModelFallback()
    {
        var retriever = new TestRetriever { Response = (_, _) => throw new HttpRequestException("Private retrieval details.") };
        var provider = new ScriptedRoleProvider();
        var failure = await Assert.ThrowsAsync<DomainException>(() => new CurriculumAgent(provider, retriever, new(), AgentTestData.Clock)
            .AnalyseAsync(AgentTestData.Request, AgentTestData.Plan, CancellationToken.None));
        Assert.Equal("agent_unavailable", failure.Code);
        Assert.DoesNotContain("Private", failure.Message);
        Assert.Empty(provider.Creations);
        Assert.Single(retriever.Calls);
    }

    [Fact]
    public async Task RetrievalDeadlineCancelsBeforeModelAnalysis()
    {
        var retriever = new TestRetriever { Response = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new RetrievedSource(AgentTestData.Source, "Unreachable evidence.");
        } };
        var provider = new ScriptedRoleProvider();
        var failure = await Assert.ThrowsAsync<DomainException>(() => new CurriculumAgent(provider, retriever,
                new() { RetrievalTimeout = TimeSpan.FromMilliseconds(30) }, AgentTestData.Clock)
            .AnalyseAsync(AgentTestData.Request, AgentTestData.Plan, CancellationToken.None));
        Assert.Equal("retrieval_timeout", failure.Code);
        Assert.Empty(provider.Creations);
    }

    [Fact]
    public async Task WriterAttachesServerProvenanceAndCannotReturnASourcesField()
    {
        var provider = ScriptedRoleProvider.Returning(LessonBody.From(AgentTestData.Lesson()));
        var writer = new LessonWriterAgent(provider, new(), AgentTestData.Clock);
        var lesson = await writer.WriteAsync(AgentTestData.Request, AgentTestData.Brief, null, null, TestContextToken());
        Assert.Same(AgentTestData.Source, Assert.Single(lesson.Sources));
        Assert.Equal(AgentRole.LessonWriter, Assert.Single(provider.Creations).Role);
        var forged = JsonSerializer.SerializeToNode(LessonBody.From(AgentTestData.Lesson()), AgentJson.Options)!.AsObject();
        forged["sources"] = JsonSerializer.SerializeToNode(AgentTestData.Brief.Sources, AgentJson.Options);
        provider.Respond = (_, _) => Task.FromResult(AgentTestData.TextResponse(forged.ToJsonString()));
        await Assert.ThrowsAsync<DomainException>(() => writer.WriteAsync(AgentTestData.Request, AgentTestData.Brief, null, null, TestContextToken()));
    }

    [Fact]
    public async Task CurriculumCannotFabricateReferencesInItsOutput()
    {
        var provider = new ScriptedRoleProvider
        {
            Respond = (_, _) => Task.FromResult(AgentTestData.TextResponse("{\"summary\":\"A sufficiently detailed curriculum summary.\",\"requirements\":[\"Trace a sorting example.\"],\"sources\":[]}"))
        };
        await Assert.ThrowsAsync<DomainException>(() => new CurriculumAgent(provider, new TestRetriever(), new(), AgentTestData.Clock)
            .AnalyseAsync(AgentTestData.Request, AgentTestData.Plan, TestContextToken()));
    }

    [Fact]
    public async Task ReviewerUsesAnIndependentFreshRoleForEachCandidate()
    {
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Accepted);
        var reviewer = new ReviewerAgent(provider, new(), AgentTestData.Clock);
        await reviewer.ReviewLessonAsync(AgentTestData.Request, AgentTestData.Lesson(), TestContextToken());
        await reviewer.ReviewQuizAsync(AgentTestData.Lesson(), AgentTestData.Quiz(), TestContextToken());
        Assert.Equal(2, provider.Creations.Count);
        Assert.All(provider.Creations, creation =>
        {
            Assert.Equal(AgentRole.Reviewer, creation.Role);
            Assert.Null(creation.Options.Tools);
            Assert.Contains("independent Reviewer", creation.Instructions);
        });
        Assert.All(provider.Requests, request => Assert.Single(request.Messages, message => message.Role == ChatRole.User));
    }

    [Fact]
    public async Task QuizDraftingIsTypedAndCannotCallPublication()
    {
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Quiz());
        var client = new TestPublicationClient();
        var result = await new QuizPublisherAgent(provider, client, new(), AgentTestData.Clock)
            .DraftAsync(AgentTestData.Lesson(), null, null, TestContextToken());
        Assert.Equal(5, result.Questions.Length);
        Assert.Equal(0, client.Calls);
        Assert.Null(Assert.Single(provider.Creations).Options.Tools);
        Assert.Equal(ChatToolMode.None, Assert.Single(provider.Creations).Options.ToolMode);
    }

    [Fact]
    public async Task InferenceFailureIsExplicitAndDoesNotReturnReplacementData()
    {
        var provider = new ScriptedRoleProvider { Respond = (_, _) => throw new HttpRequestException("secret server detail") };
        var failure = await Assert.ThrowsAsync<DomainException>(() => new OrchestratorAgent(provider, new TestRetriever(), new())
            .PlanAsync(AgentTestData.Request, TestContextToken()));
        Assert.Equal("agent_unavailable", failure.Code);
        Assert.DoesNotContain("secret", failure.Message);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ModelDeadlineCancelsTheCallAndFailsExplicitly()
    {
        var provider = new ScriptedRoleProvider { Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return AgentTestData.JsonResponse(AgentTestData.Plan);
        } };
        var failure = await Assert.ThrowsAsync<DomainException>(() => new OrchestratorAgent(provider, new TestRetriever(),
                new AgentExecutionLimits { ModelTimeout = TimeSpan.FromMilliseconds(30) })
            .PlanAsync(AgentTestData.Request, TestContextToken()));
        Assert.Equal("agent_timeout", failure.Code);
    }

    [Theory]
    [InlineData("https://resource.openai.azure.com", "https://resource.openai.azure.com/openai/v1/")]
    [InlineData("https://resource.services.ai.azure.com/openai/v1", "https://resource.services.ai.azure.com/openai/v1/")]
    public void ResponsesEndpointIsNormalisedLocally(string endpoint, string expected) =>
        Assert.Equal(expected, FoundryResponsesAgentProvider.NormaliseEndpoint(endpoint).AbsoluteUri);

    [Theory]
    [InlineData("http://resource.openai.azure.com")]
    [InlineData("https://resource.services.ai.azure.com/api/projects/test")]
    [InlineData("https://resource.openai.azure.com?key=secret")]
    [InlineData("https://user:secret@resource.openai.azure.com")]
    [InlineData("https://resource.openai.azure.com.evil.example")]
    [InlineData("https://resource.openai.azure.com:8443")]
    [InlineData("https://localhost/openai/v1/")]
    public void UnsupportedEndpointsFailClosed(string endpoint) =>
        Assert.Throws<DomainException>(() => FoundryResponsesAgentProvider.NormaliseEndpoint(endpoint));

    [Fact]
    public void ActualProviderCreatesFiveDistinctStatelessAgentsWithoutCallingTokensOrModels()
    {
        var provider = new FoundryResponsesAgentProvider(new NoNetworkCredential(), new("https://resource.openai.azure.com", "test-model"), new());
        var roles = new[] { AgentRole.Orchestrator, AgentRole.Curriculum, AgentRole.LessonWriter, AgentRole.Reviewer, AgentRole.QuizPublisher };
        var agents = roles.Select(role => provider.CreateAgent(role, "Test role instructions.", new ChatOptions { ToolMode = ChatToolMode.None })).ToArray();
        Assert.Equal(5, agents.Select(agent => agent.Name).Distinct().Count());
        Assert.All(agents, agent => Assert.StartsWith("Lumero", agent.Name));
        Assert.NotSame(agents[0], provider.CreateAgent(roles[0], "Test role instructions.", new ChatOptions()));
    }

    [Fact]
    public void DependencyInjectionRegistersProductionRolesWithExplicitExternalDependencies()
    {
        var harness = new AgentWorkflowHarness();
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(new NoNetworkCredential());
        services.AddSingleton<ICurriculumRetriever>(harness.Retriever);
        services.AddSingleton<ILessonStore>(harness.Lessons);
        services.AddSingleton<IQuizStore>(harness.Quizzes);
        services.AddSingleton<IMcpPublicationClient>(new TestPublicationClient());
        services.AddLessonPlannerAgents(new("https://resource.openai.azure.com", "test-model"));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        Assert.IsType<FoundryResponsesAgentProvider>(scope.ServiceProvider.GetRequiredService<IRoleAgentProvider>());
        Assert.IsType<OrchestratorAgent>(scope.ServiceProvider.GetRequiredService<IOrchestratorAgent>());
        Assert.IsType<CurriculumAgent>(scope.ServiceProvider.GetRequiredService<ICurriculumAgent>());
        Assert.IsType<LessonWriterAgent>(scope.ServiceProvider.GetRequiredService<ILessonWriterAgent>());
        Assert.IsType<ReviewerAgent>(scope.ServiceProvider.GetRequiredService<IReviewerAgent>());
        Assert.IsType<QuizPublisherAgent>(scope.ServiceProvider.GetRequiredService<IQuizPublisherAgent>());
        Assert.IsType<LessonWorkflow>(scope.ServiceProvider.GetRequiredService<ILessonWorkflow>());
    }

    private static CancellationToken TestContextToken() => CancellationToken.None;
}
