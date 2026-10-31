using GradingPolicyWorkshop;
using GradingPolicyWorkshop.After;
using LessonPlanner.Core;
using BeforeSession = GradingPolicyWorkshop.Before.QuizSession;
using AfterSession = GradingPolicyWorkshop.After.QuizSession;

var correctAnswers = 3;
if (args.Length > 1 || args.Length == 1 &&
    (!int.TryParse(args[0], out correctAnswers) || correctAnswers is < 0 or > ContractVersions.QuestionCount))
{
    Console.Error.WriteLine("Choose a synthetic score from 0 to 5, or omit it for 3.");
    return 2;
}

var quiz = SortingScenario.CreateQuiz();
var attempt = SortingScenario.CreateAttempt(correctAnswers);
var submittedAt = SortingScenario.SubmittedAt;

var before = new BeforeSession();
IGradingPolicy marking = new ExactMatchGradingPolicy();
IGradingPolicy feedback = new SortingFeedbackPolicy(marking);
var after = new AfterSession(feedback);

Console.WriteLine("Offline teaching example: synthetic robot sports day quiz.");
Console.WriteLine("No model calls, network access, publication or real pupil data.");
Console.WriteLine();
WriteResult("Before", before.Submit(quiz, attempt, submittedAt));
WriteResult("After", after.Submit(quiz, attempt, submittedAt));
Console.WriteLine("Both examples delegate marks to ExactMatchGradingPolicy.");
return 0;

static void WriteResult(string label, QuizResult result)
{
    Console.WriteLine($"{label}: {result.Score}/{result.Total}");
    Console.WriteLine($"Feedback: {result.Feedback}");
    Console.WriteLine();
}
