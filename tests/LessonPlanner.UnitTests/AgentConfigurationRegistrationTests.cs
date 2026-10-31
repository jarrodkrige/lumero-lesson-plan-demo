using Azure.Core;
using Azure.Identity;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LessonPlanner.UnitTests;

public sealed class AgentConfigurationRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void LocalEnvironmentsUseOnlyTheExplicitCliCredential(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(new NoNetworkCredential());
        services.AddSingleton<TokenCredential>(new NoNetworkCredential());
        Assert.Same(services, services.AddLessonAgents(Configuration(), new TestEnvironment(environmentName)));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureCliCredential>(Assert.Single(provider.GetServices<TokenCredential>()));
        Assert.IsType<FoundryResponsesAgentProvider>(provider.GetRequiredService<IRoleAgentProvider>());
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "11111111-2222-3333-4444-555555555555")]
    [InlineData("Staging", null)]
    [InlineData("Custom", "")]
    public void EveryOtherEnvironmentUsesManagedIdentityWithoutAFallback(string environmentName, string? clientId)
    {
        var services = new ServiceCollection();
        services.AddLessonAgents(Configuration("Foundry:ManagedIdentityClientId", clientId), new TestEnvironment(environmentName));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ManagedIdentityCredential>(Assert.Single(provider.GetServices<TokenCredential>()));
        Assert.IsType<FoundryResponsesAgentProvider>(provider.GetRequiredService<IRoleAgentProvider>());
    }

    [Fact]
    public void TheShimBindsEstablishedSettingsAndPreservesExistingLimitsAndClock()
    {
        var limits = new AgentExecutionLimits { ModelTimeout = TimeSpan.FromSeconds(45) };
        var harness = new AgentWorkflowHarness();
        var services = new ServiceCollection();
        services.AddSingleton(limits);
        services.AddSingleton<TimeProvider>(AgentTestData.Clock);
        services.AddSingleton<ICurriculumRetriever>(harness.Retriever);
        services.AddSingleton<ILessonStore>(harness.Lessons);
        services.AddSingleton<IQuizStore>(harness.Quizzes);
        services.AddSingleton<IMcpPublicationClient>(new TestPublicationClient());
        services.AddLessonAgents(Configuration(), new TestEnvironment("Testing"));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var settings = provider.GetRequiredService<FoundryAgentOptions>();
        Assert.Equal("https://resource.openai.azure.com", settings.Endpoint);
        Assert.Equal("lesson-deployment", settings.DeploymentName);
        Assert.Same(limits, provider.GetRequiredService<AgentExecutionLimits>());
        Assert.Same(AgentTestData.Clock, provider.GetRequiredService<TimeProvider>());
        Assert.IsType<LessonWorkflow>(scope.ServiceProvider.GetRequiredService<ILessonWorkflow>());
    }

    [Theory]
    [InlineData("Foundry:Endpoint", null)]
    [InlineData("Foundry:Endpoint", " ")]
    [InlineData("Foundry:Endpoint", "https://resource.services.ai.azure.com/api/projects/private")]
    [InlineData("Foundry:Deployment", null)]
    [InlineData("Foundry:Deployment", " ")]
    [InlineData("Foundry:Deployment", "private/model")]
    [InlineData("Foundry:ManagedIdentityClientId", "private-invalid-client")]
    [InlineData("Foundry:ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000")]
    public void InvalidConfigurationFailsBeforeChangingRegistrations(string key, string? value)
    {
        var services = new ServiceCollection();
        var credential = new NoNetworkCredential();
        services.AddSingleton<TokenCredential>(credential);
        var failure = Assert.Throws<DomainException>(() => services.AddLessonAgents(Configuration(key, value), new TestEnvironment("Testing")));
        Assert.Equal("agent_configuration", failure.Code);
        Assert.DoesNotContain("private", failure.Message);
        Assert.Same(credential, Assert.Single(services).ImplementationInstance);
    }

    [Theory]
    [InlineData("Foundry:Endpoint", 2_049)]
    [InlineData("Foundry:Deployment", 101)]
    [InlineData("Foundry:ManagedIdentityClientId", 37)]
    public void ConfigurationValuesAreBounded(string key, int length)
    {
        var services = new ServiceCollection();
        Assert.Throws<DomainException>(() => services.AddLessonAgents(Configuration(key, new string('a', length)), new TestEnvironment("Production")));
        Assert.Empty(services);
    }

    private static IConfiguration Configuration(string? key = null, string? value = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://resource.openai.azure.com",
            ["Foundry:Deployment"] = "lesson-deployment"
        };
        if (key is not null) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = nameof(AgentConfigurationRegistrationTests);
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
