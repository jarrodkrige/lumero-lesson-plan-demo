using System.Collections.Immutable;
using System.Text.Json;
using LessonPlanner.Agents;
using LessonPlanner.Core;
using Microsoft.Extensions.AI;

namespace LessonPlanner.UnitTests;

public sealed class SortingEvidenceTests
{
    private static readonly LessonRequest Request =
        new("the Tracing bubble, insertion and merge sort", "GCSE (OCR J277)", 30, null);
    private static readonly LessonRequest WorkshopRequest = new(
        "Sorting algorithms: bubble, insertion and merge sort", "GCSE (OCR J277)", 60,
        "Use the original example list 8, 3, 5, 1 to trace each algorithm. Ask pupils to explain comparisons and movements, then choose a suitable approach for another short list. Include a worked example, paired practice and an independent check.");
    private static readonly ImmutableArray<SourceReference> References = SortingTeachingSources.Required
        .Select(source => new SourceReference(source.Id, source.Title, source.Url, AgentTestData.Clock.GetUtcNow(),
            new string('a', 64), $"Synthetic test evidence for {source.Id}; it contains no numerical worked example."))
        .ToImmutableArray();

    [Theory]
    [InlineData("Tracing bubble, insertion and merge sort", "GCSE (OCR J277)", null, true)]
    [InlineData("trace bubble, insertion, and merge sort.", "GCSE (OCR J277)", "", true)]
    [InlineData("Tracing bubble, insertion and merge sort", "Key Stage 3", null, false)]
    [InlineData("Tracing bubble, insertion and merge sort", "GCSE (OCR J277)", "Use my own example.", true)]
    [InlineData("Sorting algorithms: bubble, insertion and merge sort", "GCSE (OCR J277)", "Use the original example list 8, 3, 5, 1 to trace each algorithm.", true)]
    [InlineData("  SORTING  ALGORITHMS : BUBBLE , INSERTION, AND MERGE SORT! ", "GCSE (OCR J277)", "Use paired practice.", true)]
    [InlineData("Sorting algorithms: bubble, insertion and merge sort", "Key Stage 3", null, false)]
    [InlineData("Do not trace bubble, insertion and merge sort", "GCSE (OCR J277)", null, false)]
    [InlineData("Tracing bubble, insertion and merge sort alongside binary search", "GCSE (OCR J277)", null, false)]
    [InlineData("Tracing bubble sort", "GCSE (OCR J277)", null, false)]
    public void TheEvidencePathOnlyAppliesToTheSpecificRequest(string topic, string level, string? instructions, bool expected) =>
        Assert.Equal(expected, SortingWorkedExamples.Applies(new(topic, level, 30, instructions)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredSourcesReplaceTheModelSelectionOnlyForTheSpecificRequest(bool workshop)
    {
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Plan);
        var retriever = Retriever();
        var request = workshop ? WorkshopRequest : Request;
        var result = await new OrchestratorAgent(provider, retriever, new()).PlanAsync(request, CancellationToken.None);
        Assert.Equal(SortingTeachingSources.Required.Select(source => source.Id), result.SourceIds);
        Assert.Equal(4, result.SourceIds.Distinct().Count());
        Assert.Empty(retriever.Calls);
        Assert.Contains(SortingTeachingSources.BubbleId, Input(Assert.Single(provider.Requests)), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(Input(Assert.Single(provider.Requests)));
        Assert.Equal(request.Instructions, json.RootElement.GetProperty("request").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task UnrelatedRequestsSeeTheOriginalCatalogueAndCannotSelectNewOakSources()
    {
        var provider = ScriptedRoleProvider.Returning(AgentTestData.Plan);
        var orchestrator = new OrchestratorAgent(provider, Retriever(), new());
        var result = await orchestrator.PlanAsync(AgentTestData.Request, CancellationToken.None);
        Assert.Equal(AgentTestData.Plan.SourceIds.ToArray(), result.SourceIds.ToArray());
        var input = Input(Assert.Single(provider.Requests));
        Assert.Contains(AgentTestData.Source.Id, input, StringComparison.Ordinal);
        Assert.DoesNotContain(SortingTeachingSources.BubbleId, input, StringComparison.Ordinal);
        provider.Respond = (_, _) => Task.FromResult(AgentTestData.JsonResponse(AgentTestData.Plan with
        {
            SourceIds = [SortingTeachingSources.BubbleId]
        }));
        await Assert.ThrowsAsync<DomainException>(() =>
            orchestrator.PlanAsync(AgentTestData.Request, CancellationToken.None));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("title")]
    [InlineData("url")]
    public async Task RequiredCatalogueMismatchFailsBeforeModelInvocation(string change)
    {
        var sources = SortingTeachingSources.Required.ToArray();
        if (change == "missing") sources = sources[..^1];
        else if (change == "title") sources[^1] = sources[^1] with { Title = "Different title" };
        else sources[^1] = sources[^1] with { Url = new Uri("https://example.test/other") };
        var provider = new ScriptedRoleProvider();
        var retriever = new TestRetriever { Sources = sources };
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            new OrchestratorAgent(provider, retriever, new()).PlanAsync(Request, CancellationToken.None));
        Assert.Equal("curriculum_catalogue", failure.Code);
        Assert.Empty(provider.Creations);
    }

    [Fact]
    public void AttributionRequiresTheExactTrustedSourceIdentity()
    {
        var source = References[1];
        Assert.True(SortingTeachingSources.Matches(source));
        Assert.False(SortingTeachingSources.Matches(source with { Id = "other-source" }));
        Assert.False(SortingTeachingSources.Matches(source with { Title = "Different title" }));
        Assert.False(SortingTeachingSources.Matches(source with { Url = new Uri("https://example.test/other") }));
        var matches = SortingTeachingSources.Attributions([
            source with { Url = new Uri("https://example.test/other") },
            source,
            source,
            source with { Title = "Different title" }
        ]);
        Assert.Equal(new[] { SortingTeachingSources.All[0] }, matches.ToArray());
    }

    [Fact]
    public void CalculatedExamplesContainCompleteCorrectTracesAndAreNotSourceExtracts()
    {
        var examples = Assert.IsType<SortingExampleSet>(SortingWorkedExamples.For(Request, References));
        Assert.Contains("Application-calculated", examples.Provenance, StringComparison.Ordinal);
        Assert.Equal(3, examples.Examples.Length);
        Assert.All(examples.Examples, example =>
        {
            Assert.Equal(new[] { 5, 3, 4, 1 }, example.Input);
            Assert.Equal(new[] { 1, 3, 4, 5 }, example.Result);
        });

        var bubble = examples.Examples[0];
        Assert.Equal("Bubble sort", bubble.Algorithm);
        Assert.Equal(new[]
        {
            "Pass 1: compare 5 and 3; swap: [3, 5, 4, 1].",
            "Pass 1: compare 5 and 4; swap: [3, 4, 5, 1].",
            "Pass 1: compare 5 and 1; swap: [3, 4, 1, 5].",
            "Pass 2: compare 3 and 4; leave in place: [3, 4, 1, 5].",
            "Pass 2: compare 4 and 1; swap: [3, 1, 4, 5].",
            "Pass 2: compare 4 and 5; leave in place: [3, 1, 4, 5].",
            "Pass 3: compare 3 and 1; swap: [1, 3, 4, 5].",
            "Pass 3: compare 3 and 4; leave in place: [1, 3, 4, 5].",
            "Pass 3: compare 4 and 5; leave in place: [1, 3, 4, 5].",
            "Pass 4: compare 1 and 3; leave in place: [1, 3, 4, 5].",
            "Pass 4: compare 3 and 4; leave in place: [1, 3, 4, 5].",
            "Pass 4: compare 4 and 5; leave in place: [1, 3, 4, 5].",
            "Pass 4 made no swaps, so the list is sorted."
        }, bubble.Steps);
        var insertion = examples.Examples[1];
        Assert.Equal("Insertion sort", insertion.Algorithm);
        Assert.Equal(new[]
        {
            "Start with the sorted sublist [5].",
            "Insert 3: 5 > 3, shift right; [3, 5, 4, 1].",
            "Insert 4: 5 > 4, shift right; 3 <= 4, stop; [3, 4, 5, 1].",
            "Insert 1: 5 > 1, shift right; 4 > 1, shift right; 3 > 1, shift right; [1, 3, 4, 5]."
        }, insertion.Steps);
        var merge = examples.Examples[2];
        Assert.Equal("Merge sort", merge.Algorithm);
        Assert.Equal(new[]
        {
            "Split [5, 3, 4, 1] into [5, 3] and [4, 1].",
            "Split [5, 3] into [5] and [3].",
            "Compare 5 and 3; take 3: [3].",
            "Merge [5] and [3]: [3, 5].",
            "Split [4, 1] into [4] and [1].",
            "Compare 4 and 1; take 1: [1].",
            "Merge [4] and [1]: [1, 4].",
            "Compare 3 and 1; take 1: [1].",
            "Compare 3 and 4; take 3: [1, 3].",
            "Compare 5 and 4; take 4: [1, 3, 4].",
            "Merge [3, 5] and [1, 4]: [1, 3, 4, 5]."
        }, merge.Steps);
        Assert.All(References, source => Assert.DoesNotContain("[5, 3, 4, 1]", source.Extract, StringComparison.Ordinal));
    }

    [Fact]
    public void WorkshopExamplesPreserveTheTeacherListAndContainCompleteCorrectTraces()
    {
        var examples = Assert.IsType<SortingExampleSet>(SortingWorkedExamples.For(WorkshopRequest, References));
        Assert.All(examples.Examples, example =>
        {
            Assert.Equal(new[] { 8, 3, 5, 1 }, example.Input);
            Assert.Equal(new[] { 1, 3, 5, 8 }, example.Result);
        });
        Assert.Equal(new[]
        {
            "Pass 1: compare 8 and 3; swap: [3, 8, 5, 1].",
            "Pass 1: compare 8 and 5; swap: [3, 5, 8, 1].",
            "Pass 1: compare 8 and 1; swap: [3, 5, 1, 8].",
            "Pass 2: compare 3 and 5; leave in place: [3, 5, 1, 8].",
            "Pass 2: compare 5 and 1; swap: [3, 1, 5, 8].",
            "Pass 2: compare 5 and 8; leave in place: [3, 1, 5, 8].",
            "Pass 3: compare 3 and 1; swap: [1, 3, 5, 8].",
            "Pass 3: compare 3 and 5; leave in place: [1, 3, 5, 8].",
            "Pass 3: compare 5 and 8; leave in place: [1, 3, 5, 8].",
            "Pass 4: compare 1 and 3; leave in place: [1, 3, 5, 8].",
            "Pass 4: compare 3 and 5; leave in place: [1, 3, 5, 8].",
            "Pass 4: compare 5 and 8; leave in place: [1, 3, 5, 8].",
            "Pass 4 made no swaps, so the list is sorted."
        }, examples.Examples[0].Steps);
        Assert.Equal(new[]
        {
            "Start with the sorted sublist [8].",
            "Insert 3: 8 > 3, shift right; [3, 8, 5, 1].",
            "Insert 5: 8 > 5, shift right; 3 <= 5, stop; [3, 5, 8, 1].",
            "Insert 1: 8 > 1, shift right; 5 > 1, shift right; 3 > 1, shift right; [1, 3, 5, 8]."
        }, examples.Examples[1].Steps);
        Assert.Equal(new[]
        {
            "Split [8, 3, 5, 1] into [8, 3] and [5, 1].",
            "Split [8, 3] into [8] and [3].",
            "Compare 8 and 3; take 3: [3].",
            "Merge [8] and [3]: [3, 8].",
            "Split [5, 1] into [5] and [1].",
            "Compare 5 and 1; take 1: [1].",
            "Merge [5] and [1]: [1, 5].",
            "Compare 3 and 1; take 1: [1].",
            "Compare 3 and 5; take 3: [1, 3].",
            "Compare 8 and 5; take 5: [1, 3, 5].",
            "Merge [3, 8] and [1, 5]: [1, 3, 5, 8]."
        }, examples.Examples[2].Steps);
        Assert.All(References, source => Assert.DoesNotContain("[8, 3, 5, 1]", source.Extract, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Use the ORIGINAL EXAMPLE LIST: [ 8,3, 5,1 ] to trace each algorithm.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list 8,3,5,1.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list 8, 3, 5 and 1 to trace each algorithm.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list 8, 3, 5, and 1 to trace each algorithm.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list 8 and 3 to trace each algorithm.", new[] { 8, 3 })]
    [InlineData("Use the example list [8, 3, 5 and 1] to trace each algorithm.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list 8, 3, 5, 1 and ask pupils to trace each algorithm.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list -2, +3 and -4 to trace each algorithm.", new[] { -2, 3, -4 })]
    [InlineData("Use the example list [8, 3] to trace each algorithm. Then use [5, 1] for a separate exercise.", new[] { 8, 3 })]
    [InlineData("Use the example list [8, 3], then ask pupils to sort [5, 1] independently.", new[] { 8, 3 })]
    [InlineData("Use the example list [8, 3] and ask pupils to choose [5, 1] for a separate exercise.", new[] { 8, 3 })]
    [InlineData("Use the example list [8, 3] or ask pupils to sort [5, 1] independently.", new[] { 8, 3 })]
    [InlineData("Use the example list [8, 3], then try a different list [5, 1] for practice.", new[] { 8, 3 })]
    [InlineData("Trace [8, 3, 5, 1] with paired practice.", new[] { 8, 3, 5, 1 })]
    [InlineData("Use the example list [3, -1, 3, +2, 0].", new[] { 3, -1, 3, 2, 0 })]
    [InlineData("Trace [1, 2].", new[] { 1, 2 })]
    [InlineData("Trace [8, 7, 6, 5, 4, 3, 2, 1].", new[] { 8, 7, 6, 5, 4, 3, 2, 1 })]
    [InlineData("Trace [-2147483648, 2147483647].", new[] { int.MinValue, int.MaxValue })]
    public void ExplicitTeacherListsArePreservedAcrossRelevantVariations(string instructions, int[] expected)
    {
        var request = WorkshopRequest with { Instructions = instructions };
        var examples = Assert.IsType<SortingExampleSet>(SortingWorkedExamples.For(request, References));
        Assert.All(examples.Examples, example =>
        {
            Assert.Equal(expected, example.Input);
            Assert.Equal(expected.Order().ToArray(), example.Result);
            Assert.NotEmpty(example.Steps);
        });
        Assert.Equal(instructions, request.Instructions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void BlankInstructionsRetainThePriorCalculatedExample(string? instructions)
    {
        var examples = Assert.IsType<SortingExampleSet>(SortingWorkedExamples.For(Request with { Instructions = instructions }, References));
        Assert.All(examples.Examples, example => Assert.Equal(new[] { 5, 3, 4, 1 }, example.Input));
    }

    [Theory]
    [InlineData("Use my own example.")]
    [InlineData("Use a list of names and paired practice.")]
    public void InstructionsWithoutAnExplicitNumericListReceiveEvidenceWithoutReplacementExamples(string instructions)
    {
        var request = WorkshopRequest with { Instructions = instructions };
        Assert.True(SortingWorkedExamples.Applies(request));
        Assert.Null(SortingWorkedExamples.For(request, References));
        Assert.Equal(instructions, request.Instructions);
    }

    [Theory]
    [InlineData("Use the example list [1].")]
    [InlineData("Use the example list [].")]
    [InlineData("Use the example list [1, 2.5].")]
    [InlineData("Use the example list 1, 2, then explain.")]
    [InlineData("Use the example list 8, 3, 5, 1.5 to trace each algorithm.")]
    [InlineData("Use the example list 8, 3, 5 and 1.5 to trace each algorithm.")]
    [InlineData("Use the example list 8, 3, 5 and -1.5 to trace each algorithm.")]
    [InlineData("Use the example list 8, 3 and two to trace each algorithm.")]
    [InlineData("Use the example list 8, 3, 5 and - to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] and 5 to trace each algorithm.")]
    [InlineData("Trace [8, 3] and 5 with paired practice.")]
    [InlineData("Use the example list [1, 2.")]
    [InlineData("Use the example list [1, two].")]
    [InlineData("Use the example list [1, 2147483648].")]
    [InlineData("Use the example list [1, 2, 3, 4, 5, 6, 7, 8, 9].")]
    [InlineData("Use the example list [1, 2]. Use the example list [3, 4].")]
    [InlineData("Use the example list [8, 3] or [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] OR [-5, +1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3], or [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or 5, 1 to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or the list [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or use [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or choose another list [-5, +1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or try [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] OR TRY [-5, +1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or a list [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or another set of [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or alternatively use a different list [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or choose a second sequence 5, 1 to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or consider the numbers five, one to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or try an alternative example list [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or instead take the values [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or five, one to trace each algorithm.")]
    [InlineData("Use the example list 8, 3 or 5, 1 to trace each algorithm.")]
    [InlineData("Trace [1, 2] or [3, 4].")]
    public void InvalidOrAmbiguousExplicitListsFailWithoutSubstitution(string instructions)
    {
        var failure = Assert.Throws<DomainException>(() =>
            SortingWorkedExamples.For(WorkshopRequest with { Instructions = instructions }, References));
        Assert.Equal("sorting_example", failure.Code);
        Assert.Contains("two to eight whole numbers", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Use the example list 8, 3, 5, 1.5 to trace each algorithm.")]
    [InlineData("Use the example list 8, 3, 5 and 1.5 to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or the list [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or try [5, 1] to trace each algorithm.")]
    [InlineData("Use the example list [8, 3] or a list [5, 1] to trace each algorithm.")]
    [InlineData("Trace [8, 3] and 5 with paired practice.")]
    public async Task InvalidExampleListsFailBeforeAnyModelOrRetrievalWork(string instructions)
    {
        var provider = new ScriptedRoleProvider();
        var retriever = Retriever();
        var limits = new AgentExecutionLimits();
        var calls = new List<string>();
        var lessons = new TestLessonStore(calls);
        var publication = new TestPublicationClient();
        var workflow = new LessonWorkflow(new OrchestratorAgent(provider, retriever, limits),
            new CurriculumAgent(provider, retriever, limits, AgentTestData.Clock),
            new LessonWriterAgent(provider, limits, AgentTestData.Clock),
            new ReviewerAgent(provider, limits, AgentTestData.Clock),
            new QuizPublisherAgent(provider, publication, limits, AgentTestData.Clock),
            retriever, lessons, new TestQuizStore(calls), limits, AgentTestData.Clock);

        var failure = await Assert.ThrowsAsync<DomainException>(() => workflow.GenerateAsync(
            WorkshopRequest with { Instructions = instructions }, AgentTestData.Presenter, null, CancellationToken.None));

        Assert.Equal("sorting_example", failure.Code);
        Assert.Contains("two to eight whole numbers", failure.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Creations);
        Assert.Empty(provider.Requests);
        Assert.Empty(retriever.Calls);
        Assert.Empty(lessons.Writes);
        Assert.Equal(0, publication.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExamplesRequireAllExactlyBoundSources(bool workshop)
    {
        var request = workshop ? WorkshopRequest : Request;
        Assert.Null(SortingWorkedExamples.For(request, References.RemoveAt(3)));
        Assert.Null(SortingWorkedExamples.For(request, References.SetItem(3,
            References[3] with { Url = new Uri("https://example.test/incorrect") })));
        Assert.Null(SortingWorkedExamples.For(request with { Instructions = "Use different data." }, References));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RealRoleCoordinationSuppliesSeparateEvidenceAndStillHonoursIndependentReview(bool accepted, bool workshop)
    {
        var request = workshop ? WorkshopRequest : Request;
        var provider = new ScriptedRoleProvider
        {
            Respond = (role, _) => Task.FromResult(role switch
            {
                AgentRole.Orchestrator => AgentTestData.JsonResponse(AgentTestData.Plan),
                AgentRole.Curriculum => AgentTestData.JsonResponse(new CurriculumAnalysis(
                    "Synthetic curriculum analysis for tracing the three sorting procedures.",
                    ["Trace the three procedures and explain each change."])),
                AgentRole.LessonWriter => AgentTestData.JsonResponse(LessonBody.From(Lesson(request))),
                AgentRole.Reviewer => AgentTestData.JsonResponse(accepted ? AgentTestData.Accepted :
                    new ReviewResult(false, [new("trace", ReviewSeverity.Revision, "The candidate trace needs correction.")])),
                _ => throw new InvalidOperationException("Unexpected role in synthetic verification.")
            })
        };
        var retriever = Retriever();
        var limits = new AgentExecutionLimits();
        var calls = new List<string>();
        var lessons = new TestLessonStore(calls);
        var publication = new TestPublicationClient();
        var workflow = new LessonWorkflow(new OrchestratorAgent(provider, retriever, limits),
            new CurriculumAgent(provider, retriever, limits, AgentTestData.Clock),
            new LessonWriterAgent(provider, limits, AgentTestData.Clock),
            new ReviewerAgent(provider, limits, AgentTestData.Clock),
            new QuizPublisherAgent(provider, publication, limits, AgentTestData.Clock),
            retriever, lessons, new TestQuizStore(calls), limits, AgentTestData.Clock);
        var record = await workflow.GenerateAsync(request, AgentTestData.Presenter, null, CancellationToken.None);

        Assert.Equal(accepted ? RunStatus.Ready : RunStatus.Rejected, record.Status);
        Assert.Equal(SortingTeachingSources.Required.Select(source => source.Id), retriever.Calls);
        Assert.Equal(accepted ? 1 : 3, provider.Requests.Count(request => request.Role == AgentRole.Reviewer));
        Assert.Equal(accepted, record.Lesson is not null);
        Assert.Single(lessons.Writes);
        Assert.Equal(request, record.Request);
        Assert.Equal(0, publication.Calls);
        var writerInput = Input(provider.Requests.First(request => request.Role == AgentRole.LessonWriter));
        var reviewerInput = Input(provider.Requests.First(request => request.Role == AgentRole.Reviewer));
        using var writerJson = JsonDocument.Parse(writerInput);
        using var reviewerJson = JsonDocument.Parse(reviewerInput);
        var writerExamples = writerJson.RootElement.GetProperty("applicationCalculatedExamples");
        var reviewerExamples = reviewerJson.RootElement.GetProperty("applicationCalculatedExamples");
        Assert.Equal(writerExamples.GetRawText(), reviewerExamples.GetRawText());
        var expectedInput = workshop ? new[] { 8, 3, 5, 1 } : new[] { 5, 3, 4, 1 };
        foreach (var example in writerExamples.GetProperty("examples").EnumerateArray())
            Assert.Equal(expectedInput, example.GetProperty("input").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(request.Instructions, writerJson.RootElement.GetProperty("request").GetProperty("instructions").GetString());
        Assert.Equal(request.Instructions, reviewerJson.RootElement.GetProperty("request").GetProperty("instructions").GetString());
        Assert.Contains("Application-calculated", writerExamples.GetProperty("provenance").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[5, 3, 4, 1]", writerJson.RootElement.GetProperty("curriculum").GetProperty("evidence").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("[5, 3, 4, 1]", reviewerJson.RootElement.GetProperty("evidence").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("complete worked trace for each of the", provider.Creations.First(creation => creation.Role == AgentRole.LessonWriter).Instructions,
            StringComparison.Ordinal);
        Assert.Contains("each of the three supplied examples", provider.Creations.First(creation => creation.Role == AgentRole.Reviewer).Instructions,
            StringComparison.Ordinal);
        Assert.Contains("independently", provider.Creations.First(creation => creation.Role == AgentRole.Reviewer).Instructions,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkshopQuizDraftAndReviewRetainCalculatedLessonEvidenceWithoutPublication(bool accepted)
    {
        var reviewCount = 0;
        var provider = new ScriptedRoleProvider
        {
            Respond = (role, _) => Task.FromResult(role switch
            {
                AgentRole.Orchestrator => AgentTestData.JsonResponse(AgentTestData.Plan),
                AgentRole.Curriculum => AgentTestData.JsonResponse(new CurriculumAnalysis(
                    "Synthetic curriculum analysis for the workshop sorting lesson.", ["Trace and explain the three procedures."])),
                AgentRole.LessonWriter => AgentTestData.JsonResponse(LessonBody.From(Lesson(WorkshopRequest))),
                AgentRole.QuizPublisher => AgentTestData.JsonResponse(Quiz()),
                AgentRole.Reviewer => AgentTestData.JsonResponse(++reviewCount == 1 || accepted ? AgentTestData.Accepted :
                    new ReviewResult(false, [new("answer_key", ReviewSeverity.Revision, "Correct the numerical answer against the calculated lesson trace.")])),
                _ => throw new InvalidOperationException("Unexpected role in synthetic verification.")
            })
        };
        var retriever = Retriever();
        var limits = new AgentExecutionLimits();
        var calls = new List<string>();
        var quizzes = new TestQuizStore(calls);
        var publication = new TestPublicationClient();
        var workflow = new LessonWorkflow(new OrchestratorAgent(provider, retriever, limits),
            new CurriculumAgent(provider, retriever, limits, AgentTestData.Clock),
            new LessonWriterAgent(provider, limits, AgentTestData.Clock),
            new ReviewerAgent(provider, limits, AgentTestData.Clock),
            new QuizPublisherAgent(provider, publication, limits, AgentTestData.Clock),
            retriever, new TestLessonStore(calls), quizzes, limits, AgentTestData.Clock);
        var record = await workflow.GenerateAsync(WorkshopRequest, AgentTestData.Presenter, null, CancellationToken.None);
        Assert.Equal(RunStatus.Ready, record.Status);
        if (accepted)
        {
            var draft = await workflow.DraftQuizAsync(record.Id, AgentTestData.Presenter, null, CancellationToken.None);
            Assert.Equal(5, draft.Content.Questions.Length);
            Assert.Single(quizzes.Writes);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DomainException>(() =>
                workflow.DraftQuizAsync(record.Id, AgentTestData.Presenter, null, CancellationToken.None));
            Assert.Equal("quiz_rejected", failure.Code);
            Assert.Empty(quizzes.Writes);
        }
        var drafts = provider.Requests.Where(request => request.Role == AgentRole.QuizPublisher).ToArray();
        var reviews = provider.Requests.Where(request => request.Role == AgentRole.Reviewer).Skip(1).ToArray();
        Assert.Equal(accepted ? 1 : 3, drafts.Length);
        Assert.Equal(drafts.Length, reviews.Length);
        foreach (var input in drafts.Concat(reviews).Select(Input))
        {
            using var json = JsonDocument.Parse(input);
            var lesson = json.RootElement.GetProperty("lesson");
            Assert.Equal(record.Lesson!.Explanation, lesson.GetProperty("explanation").GetString());
            Assert.Contains("Application-calculated", lesson.GetProperty("explanation").GetString(), StringComparison.Ordinal);
            Assert.Contains("[8, 3, 5, 1]", lesson.GetProperty("explanation").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("[5, 3, 4, 1]", lesson.GetRawText(), StringComparison.Ordinal);
            var evidence = json.RootElement.GetProperty("evidence");
            Assert.Equal(References.Select(source => source.Id), evidence.EnumerateArray().Select(source => source.GetProperty("id").GetString()));
            Assert.DoesNotContain("[8, 3, 5, 1]", evidence.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Contains("independently calculate the answer", provider.Creations.First(creation => creation.Role == AgentRole.QuizPublisher).Instructions,
            StringComparison.Ordinal);
        Assert.Contains("Independently calculate numerical answers", provider.Creations.First(creation => creation.Role == AgentRole.Reviewer).Instructions,
            StringComparison.Ordinal);
        Assert.Equal(0, publication.Calls);
    }

    private static QuizContent Quiz()
    {
        (string Prompt, string Answer, string B, string C, string D, string Source)[] questions =
        [
            ("What is the ascending result?", "[1, 3, 5, 8]", "[3, 1, 5, 8]", "[1, 5, 3, 8]", "[8, 5, 3, 1]", SortingTeachingSources.BubbleId),
            ("Which pair does bubble sort compare first?", "8 and 3", "3 and 5", "5 and 1", "8 and 1", SortingTeachingSources.BubbleId),
            ("What remains after the first bubble sort pass?", "[3, 5, 1, 8]", "[1, 3, 5, 8]", "[3, 8, 5, 1]", "[8, 3, 1, 5]", SortingTeachingSources.BubbleId),
            ("What remains after inserting 3 into the sorted sublist?", "[3, 8, 5, 1]", "[1, 3, 5, 8]", "[3, 5, 8, 1]", "[8, 3, 1, 5]", SortingTeachingSources.InsertionId),
            ("What is the first merge sort split?", "[8, 3] and [5, 1]", "[8] and [3, 5, 1]", "[8, 3, 5] and [1]", "[8, 5] and [3, 1]", SortingTeachingSources.MergeId)
        ];
        return new("Workshop sorting check", questions.Select((question, index) =>
            new DraftQuestion($"q{index + 1}", $"For the application-calculated list [8, 3, 5, 1]: {question.Prompt}",
                [new("a", question.Answer), new("b", question.B), new("c", question.C), new("d", question.D)], "a",
                $"The application-calculated trace gives {question.Answer} using the cited procedure.", [question.Source])).ToImmutableArray());
    }

    private static string Input(TestAgentRequest request) =>
        Assert.Single(request.Messages, message => message.Role == ChatRole.User).Text;

    private static TestRetriever Retriever() => new()
    {
        Sources = [new(AgentTestData.Source.Id, AgentTestData.Source.Title, AgentTestData.Source.Url), .. SortingTeachingSources.Required],
        Response = (id, _) =>
        {
            var source = References.Single(reference => reference.Id == id);
            return Task.FromResult(new RetrievedSource(source, source.Extract));
        }
    };

    private static LessonPlan Lesson(LessonRequest request) => new("Tracing the three sorting algorithms",
        "Trace three sorting procedures with independently calculated examples.",
        [new("Explain the bubble sort procedure.", [SortingTeachingSources.BubbleId]),
         new("Explain the insertion sort procedure.", [SortingTeachingSources.InsertionId]),
         new("Explain the merge sort procedure.", [SortingTeachingSources.MergeId])],
        ["Compare whole numbers."],
        string.Join("\n", SortingWorkedExamples.For(request, References)!.Examples.Select(example =>
            $"Application-calculated {example.Algorithm} example [{string.Join(", ", example.Input)}]:\n{string.Join("\n", example.Steps)}")),
        [new("Trace examples", request.DurationMinutes, request.Instructions ?? "Work through each example and check each comparison.", "Explain the next comparison.")],
        ["What is compared first?", "How does the list become sorted?"], ["Number cards"], References);
}
