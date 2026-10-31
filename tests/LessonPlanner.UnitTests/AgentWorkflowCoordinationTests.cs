using System.Collections.Immutable;
using LessonPlanner.Agents;
using LessonPlanner.Core;

namespace LessonPlanner.UnitTests;

public class AgentWorkflowCoordinationTests
{
    [Fact]
    public async Task LessonRunsFixedRolesAndPersistsOneFinalAcceptedRecord()
    {
        var harness = new AgentWorkflowHarness();
        var progress = new TestActivitySink(activity =>
        {
            if (activity.Role == AgentRole.Persistence && activity.State == ActivityState.Succeeded)
                Assert.Single(harness.Lessons.Writes);
        });
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, progress, CancellationToken.None);
        Assert.Equal(new[] { "plan", "curriculum", "write", "review-lesson", "save-lesson" }, harness.Calls);
        Assert.Equal(RunStatus.Ready, record.Status);
        Assert.NotNull(record.Lesson);
        Assert.True(DomainRules.ReviewAccepted(record.Review));
        Assert.Same(record, Assert.Single(harness.Lessons.Writes));
        Assert.All(record.Activities, activity =>
        {
            Assert.Equal(record.Id, activity.CorrelationId);
            Assert.InRange(activity.Summary.Length, 1, 300);
            Assert.True(Enum.IsDefined(activity.Role));
            Assert.True(Enum.IsDefined(activity.State));
        });
        Assert.DoesNotContain(record.Activities, activity => activity.Role == AgentRole.Persistence);
        Assert.Null(Assert.Single(harness.LessonInputs).Previous);
        Assert.Null(Assert.Single(harness.LessonInputs).Feedback);
    }

    [Fact]
    public async Task DeterministicLessonFailuresCannotBeOverriddenByAReviewer()
    {
        var harness = new AgentWorkflowHarness { Write = (_, _) => Task.FromResult(InvalidTiming()) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Rejected, record.Status);
        Assert.Null(record.Lesson);
        Assert.Equal(3, harness.LessonInputs.Count);
        Assert.DoesNotContain("review-lesson", harness.Calls);
        Assert.Single(harness.Lessons.Writes);
        Assert.All(harness.LessonInputs.Skip(1), input =>
        {
            Assert.NotNull(input.Previous);
            Assert.False(input.Feedback!.Accepted);
            Assert.Contains(input.Feedback.Findings, finding => finding.Code == "deterministic_validation");
        });
    }

    [Fact]
    public async Task ReviewerHasAtMostTheOriginalAndTwoRevisions()
    {
        var harness = new AgentWorkflowHarness { LessonReview = (_, _) => Task.FromResult(AgentTestData.Revision) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Rejected, record.Status);
        Assert.Equal(3, harness.LessonInputs.Count);
        Assert.Equal(3, harness.Calls.Count(call => call == "review-lesson"));
        Assert.Single(harness.Lessons.Writes);
        Assert.Null(record.Lesson);
    }

    [Fact]
    public async Task DeterministicAndModelRejectionsShareOneGlobalRevisionBudget()
    {
        var harness = new AgentWorkflowHarness
        {
            Write = (attempt, _) => Task.FromResult(attempt == 1 ? InvalidTiming() : AgentTestData.Lesson()),
            LessonReview = (attempt, _) => Task.FromResult(attempt == 1 ? AgentTestData.Revision : AgentTestData.Accepted)
        };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Ready, record.Status);
        Assert.Equal(3, harness.LessonInputs.Count);
        Assert.Equal(2, harness.Calls.Count(call => call == "review-lesson"));
        Assert.Single(harness.Lessons.Writes);
    }

    [Fact]
    public async Task WorkflowRestampsWriterSourcesFromTrustedCurriculum()
    {
        var forged = AgentTestData.Source with { Url = new Uri("https://forged.example.test/spec.pdf"), Sha256 = new string('f', 64) };
        var harness = new AgentWorkflowHarness { Write = (_, _) => Task.FromResult(AgentTestData.Lesson() with { Sources = [forged] }) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Ready, record.Status);
        Assert.Same(AgentTestData.Source, Assert.Single(record.Lesson!.Sources));
        Assert.NotEqual(forged.Sha256, record.Lesson.Sources[0].Sha256);
    }

    [Fact]
    public async Task UnknownLessonCitationCannotReachIndependentAcceptance()
    {
        var harness = new AgentWorkflowHarness { Write = (_, _) => Task.FromResult(AgentTestData.Lesson() with
        {
            Objectives = [new("Explain a comparison step.", ["invented-source"])]
        }) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Rejected, record.Status);
        Assert.DoesNotContain("review-lesson", harness.Calls);
        Assert.Equal(3, harness.LessonInputs.Count);
    }

    [Fact]
    public async Task ContradictoryAcceptedFlagCannotOverrideCriticalFindings()
    {
        var harness = new AgentWorkflowHarness { LessonReview = (_, _) => Task.FromResult(new ReviewResult(true,
            [new("correctness", ReviewSeverity.Critical, "The explanation contradicts the supplied evidence.")])) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Rejected, record.Status);
        Assert.Null(record.Lesson);
        Assert.Equal(3, harness.LessonInputs.Count);
    }

    [Fact]
    public async Task MalformedReviewFailsClosedWithoutRepeatedInference()
    {
        var harness = new AgentWorkflowHarness { LessonReview = (_, _) => Task.FromResult(new ReviewResult(true, [null!])) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Null(record.Lesson);
        Assert.Null(record.Review);
        Assert.Single(harness.LessonInputs);
        Assert.Single(harness.Lessons.Writes);
    }

    [Fact]
    public async Task ProviderFailurePersistsOnlyOneSafeFailureTrace()
    {
        const string sensitiveDetail = "secret prompt and token content";
        var harness = new AgentWorkflowHarness { Write = (_, _) => throw new HttpRequestException(sensitiveDetail) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Null(record.Lesson);
        Assert.Single(harness.Lessons.Writes);
        Assert.DoesNotContain("review-lesson", harness.Calls);
        Assert.All(record.Activities, activity => Assert.DoesNotContain(sensitiveDetail, activity.Summary));
        Assert.Contains(record.Activities, activity => activity.Role == AgentRole.LessonWriter && activity.State == ActivityState.Failed);
    }

    [Fact]
    public async Task RetrievalFailureDoesNotInvokeWriterOrInventFallbackEvidence()
    {
        var harness = new AgentWorkflowHarness { Curriculum = _ => throw new DomainException("retrieval_unavailable", "Private details") };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Empty(harness.LessonInputs);
        Assert.Null(record.Lesson);
        Assert.Single(harness.Lessons.Writes);
    }

    [Fact]
    public async Task MismatchedCurriculumCannotReachWriter()
    {
        var harness = new AgentWorkflowHarness { Curriculum = _ => Task.FromResult(AgentTestData.Brief with
        {
            Sources = [AgentTestData.Source with { Id = "different-source" }]
        }) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Empty(harness.LessonInputs);
    }

    [Fact]
    public async Task CoordinatorIndependentlyChecksCanonicalCurriculumBinding()
    {
        var harness = new AgentWorkflowHarness { Curriculum = _ => Task.FromResult(AgentTestData.Brief with
        {
            Sources = [AgentTestData.Source with { Url = new Uri("https://replacement.example.test/source.pdf") }]
        }) };
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Empty(harness.LessonInputs);
        Assert.Single(harness.Lessons.Writes);
    }

    [Fact]
    public async Task ADisconnectedObserverDoesNotChangeAcceptanceOrLoseTheFinalTrace()
    {
        var harness = new AgentWorkflowHarness();
        var progress = new TestActivitySink(_ => throw new IOException("Observer disconnected."));
        var record = await harness.Create().GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, progress, CancellationToken.None);
        Assert.Equal(RunStatus.Ready, record.Status);
        Assert.NotEmpty(record.Activities);
        Assert.Same(record, Assert.Single(harness.Lessons.Writes));
    }

    [Fact]
    public async Task InvalidRequestDoesNotInvokeOrPersistAnything()
    {
        var harness = new AgentWorkflowHarness();
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().GenerateAsync(AgentTestData.Request with { DurationMinutes = 2 },
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Empty(harness.Calls);
        Assert.Empty(harness.Lessons.Writes);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedWithoutSavingACompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new AgentWorkflowHarness { Write = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<LessonPlan>(token);
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Create().GenerateAsync(AgentTestData.Request,
            AgentTestData.Presenter, null, cancellation.Token));
        Assert.Empty(harness.Lessons.Writes);
        Assert.DoesNotContain("review-lesson", harness.Calls);
    }

    [Fact]
    public async Task WorkflowDeadlineProducesAnImmutableFailureInsteadOfASuccess()
    {
        var harness = new AgentWorkflowHarness { Write = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return AgentTestData.Lesson();
        } };
        var record = await harness.Create(new() { WorkflowTimeout = TimeSpan.FromMilliseconds(30) })
            .GenerateAsync(AgentTestData.Request, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Single(harness.Lessons.Writes);
        Assert.Null(record.Lesson);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotReturnReadyOrEmitPersistenceSuccess()
    {
        var harness = new AgentWorkflowHarness();
        harness.Lessons.SaveFailure = new IOException("private storage failure");
        var progress = new TestActivitySink();
        var failure = await Assert.ThrowsAsync<DomainException>(() => harness.Create().GenerateAsync(AgentTestData.Request,
            AgentTestData.Presenter, progress, CancellationToken.None));
        Assert.Equal("lesson_storage", failure.Code);
        Assert.Empty(harness.Lessons.Writes);
        Assert.DoesNotContain(progress.Events, activity => activity.Role == AgentRole.Persistence && activity.State == ActivityState.Succeeded);
    }

    [Fact]
    public async Task AcceptedQuizIsSavedOnlyAfterIndependentReviewWithServerIdentityAndHash()
    {
        var harness = QuizHarness();
        var progress = new TestActivitySink();
        var draft = await harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id, AgentTestData.Presenter, progress, CancellationToken.None);
        Assert.Equal(new[] { "load-lesson", "draft", "review-quiz", "save-quiz" }, harness.Calls);
        Assert.Same(draft, Assert.Single(harness.Quizzes.Writes));
        Assert.Equal(1, draft.Version);
        Assert.Equal(AgentTestData.Presenter, draft.Owner);
        Assert.Equal(harness.Lessons.Stored.Id, draft.LessonId);
        Assert.True(DomainRules.IsId(draft.Id));
        Assert.Equal(DomainRules.DraftHash(draft), draft.PayloadHash);
        Assert.Equal(DomainRules.Hash(harness.Lessons.Stored.Lesson!.Sources), DomainRules.Hash(draft.Sources));
        Assert.Equal(5, draft.Content.Questions.Length);
        Assert.True(DomainRules.ReviewAccepted(draft.Review));
        Assert.Empty(harness.Lessons.Writes);
        Assert.Equal(ActivityState.AwaitingApproval, progress.Events[^1].State);
    }

    [Fact]
    public async Task InvalidQuestionCountCannotBeOverriddenAndIsBounded()
    {
        var harness = QuizHarness();
        harness.Draft = (_, _) => Task.FromResult(AgentTestData.Quiz(4));
        var failure = await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Equal("quiz_rejected", failure.Code);
        Assert.Equal(3, harness.QuizInputs.Count);
        Assert.DoesNotContain("review-quiz", harness.Calls);
        Assert.Empty(harness.Quizzes.Writes);
        Assert.Empty(harness.Lessons.Writes);
    }

    [Fact]
    public async Task QuizReviewCannotExtendTheTwoRevisionLimit()
    {
        var harness = QuizHarness();
        harness.QuizReview = (_, _) => Task.FromResult(AgentTestData.Revision);
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Equal(3, harness.QuizInputs.Count);
        Assert.Equal(3, harness.Calls.Count(call => call == "review-quiz"));
        Assert.All(harness.QuizInputs.Skip(1), input => { Assert.NotNull(input.Previous); Assert.False(input.Feedback!.Accepted); });
        Assert.Empty(harness.Quizzes.Writes);
    }

    [Fact]
    public async Task QuizDeterministicAndModelChecksShareOneRevisionBudget()
    {
        var harness = QuizHarness();
        harness.Draft = (attempt, _) => Task.FromResult(AgentTestData.Quiz(attempt == 1 ? 4 : 5));
        harness.QuizReview = (attempt, _) => Task.FromResult(attempt == 1 ? AgentTestData.Revision : AgentTestData.Accepted);
        var draft = await harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(3, harness.QuizInputs.Count);
        Assert.Equal(2, harness.Calls.Count(call => call == "review-quiz"));
        Assert.Same(draft, Assert.Single(harness.Quizzes.Writes));
    }

    [Fact]
    public async Task InvalidQuizCitationPreventsReviewAndSave()
    {
        var harness = QuizHarness();
        harness.Draft = (_, _) => Task.FromResult(AgentTestData.Quiz() with
        {
            Questions = AgentTestData.Quiz().Questions.Select(question => question with { SourceIds = ["unknown-source"] }).ToImmutableArray()
        });
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.DoesNotContain("review-quiz", harness.Calls);
        Assert.Empty(harness.Quizzes.Writes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("status")]
    [InlineData("review")]
    [InlineData("schema")]
    [InlineData("missing")]
    public async Task QuizRequiresAnExistingAcceptedPresenterOwnedLesson(string invalid)
    {
        var harness = QuizHarness();
        var id = harness.Lessons.Stored!.Id;
        harness.Lessons.Stored = invalid switch
        {
            "owner" => harness.Lessons.Stored with { Owner = AgentTestData.Presenter with { ObjectId = Guid.NewGuid() } },
            "status" => harness.Lessons.Stored with { Status = RunStatus.Rejected },
            "review" => harness.Lessons.Stored with { Review = AgentTestData.Revision },
            "schema" => harness.Lessons.Stored with { SchemaVersion = "unknown.v9" },
            _ => null
        };
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(id, AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Empty(harness.QuizInputs);
        Assert.Empty(harness.Quizzes.Writes);
    }

    [Fact]
    public async Task EachNewQuizDraftHasAnIndependentUnpredictableIdentityAtVersionOne()
    {
        var harness = QuizHarness();
        var workflow = harness.Create();
        var first = await workflow.DraftQuizAsync(harness.Lessons.Stored!.Id, AgentTestData.Presenter, null, CancellationToken.None);
        var second = await workflow.DraftQuizAsync(harness.Lessons.Stored.Id, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.PayloadHash, second.PayloadHash);
        Assert.Equal(1, first.Version);
        Assert.Equal(1, second.Version);
        Assert.Empty(harness.Lessons.Writes);
    }

    [Fact]
    public async Task QuizInferenceFailureAndMalformedReviewAreNotSaved()
    {
        var harness = QuizHarness();
        harness.Draft = (_, _) => throw new HttpRequestException("private provider detail");
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Empty(harness.Quizzes.Writes);
        harness.Draft = (_, _) => Task.FromResult(AgentTestData.Quiz());
        harness.QuizReview = (_, _) => Task.FromResult(new ReviewResult(true, [null!]));
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, CancellationToken.None));
        Assert.Empty(harness.Quizzes.Writes);
    }

    [Fact]
    public async Task QuizCancellationDuringIndependentReviewDoesNotSave()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = QuizHarness();
        harness.QuizReview = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ReviewResult>(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, null, cancellation.Token));
        Assert.Empty(harness.Quizzes.Writes);
        Assert.Empty(harness.Lessons.Writes);
    }

    [Fact]
    public async Task QuizPersistenceFailureDoesNotClaimASavedDraftOrApproval()
    {
        var harness = QuizHarness();
        harness.Quizzes.SaveFailure = new IOException("private storage detail");
        var progress = new TestActivitySink();
        await Assert.ThrowsAsync<DomainException>(() => harness.Create().DraftQuizAsync(harness.Lessons.Stored!.Id,
            AgentTestData.Presenter, progress, CancellationToken.None));
        Assert.Empty(harness.Quizzes.Writes);
        Assert.DoesNotContain(progress.Events, activity => activity.State == ActivityState.AwaitingApproval);
    }

    private static LessonPlan InvalidTiming() => AgentTestData.Lesson() with
    {
        Activities = [new("Short practice", 20, "Trace a small sorting example together.", "Explain a swap.")]
    };

    private static AgentWorkflowHarness QuizHarness()
    {
        var harness = new AgentWorkflowHarness();
        harness.Lessons.Stored = AgentTestData.ReadyRecord();
        return harness;
    }
}
