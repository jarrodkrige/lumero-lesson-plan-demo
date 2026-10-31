using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LessonPlanner.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LessonPlanner.IntegrationTests;

// Fixed model replacements are confined to the local test assembly. Production has no equivalent registration.
internal static class LocalVerificationRoles
{
    private const string SourceText = "Synthetic local-test provenance. No claim of live retrieval is made.";
    private static readonly ImmutableArray<SourceReference> Sources =
        [TestData.Sources[0] with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceText))) }];

    public static void Register(IServiceCollection services, IMcpPublicationClient realTransport)
    {
        services.RemoveAll<IOrchestratorAgent>();
        services.RemoveAll<ICurriculumAgent>();
        services.RemoveAll<ILessonWriterAgent>();
        services.RemoveAll<IReviewerAgent>();
        services.RemoveAll<IQuizPublisherAgent>();
        services.RemoveAll<ICurriculumRetriever>();
        services.RemoveAll<IMcpPublicationClient>();
        services.AddSingleton<LocalVerificationCounters>();
        services.AddSingleton<IOrchestratorAgent, LocalOrchestrator>();
        services.AddSingleton<ICurriculumAgent, LocalCurriculum>();
        services.AddSingleton<ILessonWriterAgent, LocalWriter>();
        services.AddSingleton<IReviewerAgent, LocalReviewer>();
        services.AddSingleton<IQuizPublisherAgent, LocalPublisher>();
        services.AddSingleton<ICurriculumRetriever, LocalSource>();
        services.AddSingleton(realTransport);
    }

    private sealed class LocalOrchestrator : IOrchestratorAgent
    {
        public Task<OrchestrationPlan> PlanAsync(LessonRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OrchestrationPlan(["ocr-j277"], "Trace comparisons and explain the ordering of values.",
                "Use a fixed local verification activity with number cards and paired explanations."));
    }

    private sealed class LocalCurriculum : ICurriculumAgent
    {
        public Task<CurriculumBrief> AnalyseAsync(LessonRequest request, OrchestrationPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(new CurriculumBrief("Fixed curriculum analysis for local verification only.",
                ["Trace the adjacent comparisons in a sorting pass."], Sources));
    }

    private sealed class LocalWriter : ILessonWriterAgent
    {
        public Task<LessonPlan> WriteAsync(LessonRequest request, CurriculumBrief curriculum, LessonPlan? previous,
            ReviewResult? feedback, CancellationToken cancellationToken)
        {
            var template = TestData.Lesson().Lesson!;
            return Task.FromResult(template with
            {
                Title = $"Local verification: {request.Topic}",
                Overview = "This fixed lesson verifies the application workflow. It is not model-generated classroom material.",
                Activities = [template.Activities[0] with { DurationMinutes = request.DurationMinutes }],
                Sources = curriculum.Sources
            });
        }
    }

    private sealed class LocalReviewer : IReviewerAgent
    {
        public Task<ReviewResult> ReviewLessonAsync(LessonRequest request, LessonPlan lesson, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewResult(true, []));
        public Task<ReviewResult> ReviewQuizAsync(LessonPlan lesson, QuizContent quiz, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewResult(true, []));
    }

    private sealed class LocalPublisher(IMcpPublicationClient transport, LocalVerificationCounters counters) : IQuizPublisherAgent
    {
        public Task<QuizContent> DraftAsync(LessonPlan lesson, QuizContent? previous, ReviewResult? feedback, CancellationToken cancellationToken) =>
            Task.FromResult(TestData.Draft(TestData.Lesson()).Content with { Title = "Local verification: five sorting questions" });
        public async Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref counters.TransportCalls);
            var result = await transport.PublishAsync(approval, caller, cancellationToken);
            Interlocked.Increment(ref counters.ConfirmedPublications);
            return result;
        }
    }

    private sealed class LocalSource : ICurriculumRetriever
    {
        public IReadOnlyList<CurriculumSource> Sources { get; } =
            [new("ocr-j277", "Synthetic curriculum source for local tests", TestData.Sources[0].Url)];
        public Task<RetrievedSource> RetrieveAsync(string sourceId, CancellationToken cancellationToken) =>
            sourceId == "ocr-j277" ? Task.FromResult(new RetrievedSource(LocalVerificationRoles.Sources[0], SourceText)) :
                throw new DomainException("source_not_allowed", "Unknown local verification source.");
    }
}

internal sealed class LocalVerificationCounters
{
    public int TransportCalls;
    public int ConfirmedPublications;
}
