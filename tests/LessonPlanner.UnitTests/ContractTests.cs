using System.Text.Json;
using LessonPlanner.Core;
using LessonPlanner.Agents;

namespace LessonPlanner.UnitTests;

public class ContractTests
{
    [Fact]
    public void PinnedStackTypesLoad() => Assert.Equal(3, StackCompatibility.ContractTypes.Count);

    [Fact]
    public void PublicQuizHasNoPrivateMarkingProperties()
    {
        var quiz = new PublicQuiz(DomainRules.NewId(), "Sorting quiz", [new("q1", "Which robot finishes first?", [new("a", "Ada")])], DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(quiz, DomainRules.Json);
        Assert.DoesNotContain("correct", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answerKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(121)]
    public void DurationIsBounded(int duration) => Assert.False(DomainRules.ValidateRequest(new("Sorting", "GCSE (OCR J277)", duration, null)).IsValid);

    [Fact]
    public void ValidRequestPasses() => Assert.True(DomainRules.ValidateRequest(new("Sorting algorithms", "GCSE (OCR J277)", 60, "Robot sports day")).IsValid);

    [Fact]
    public void IdentifiersAreUnpredictableAndWellFormed()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => DomainRules.NewId()).ToArray();
        Assert.All(ids, id => Assert.True(DomainRules.IsId(id)));
        Assert.Equal(100, ids.Distinct().Count());
    }
}
