# Offline C# grading-policy workshop

Original teaching material for six secondary computer science teachers attending a 90-minute workshop on Monday 2 November 2026. This example supplies a compact live-review activity within that workshop, not a replacement for the full session agenda.

**This is an offline teaching example, not a model, deployed application, live assessment or approved lesson.** It makes no network calls and needs no credentials. All questions, robot times and feedback in this directory are synthetic and newly written. No private plan, brief or reference material is reproduced.

## What is here

- `GradingPolicyWorkshop\Before\QuizSession.cs`: a working class that chooses a concrete marker and contains its own feedback rule.
- `GradingPolicyWorkshop\After\QuizSession.cs`: the same operation with a constructor-injected `IGradingPolicy`.
- `GradingPolicyWorkshop\After\SortingFeedbackPolicy.cs`: an explicit workshop feedback policy that wraps the real marker.
- `GradingPolicyWorkshop\SortingScenario.cs`: five original single-choice sorting questions, four options per question and synthetic attempts with a chosen number of correct answers.
- `GradingPolicyWorkshop\Program.cs`: manual dependency composition and a deterministic console demonstration.
- `lesson-inputs\ocr-j277-sorting.json` and `lesson-inputs\robot-sports-day.json`: original lesson requests, not generated lesson outputs.
- `..\tests\LessonPlanner.UnitTests\GradingPolicyWorkshopExampleTests.cs`: focused tests using the repository's existing xUnit packages.

## Run the example

Run these PowerShell commands from the repository root with the pinned .NET SDK **10.0.403** installed. The example targets `net10.0`, inherits warnings-as-errors and references only `LessonPlanner.Core`. It adds no NuGet packages.

```powershell
dotnet restore .\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj --locked-mode --source .\examples -p:NuGetAudit=false
dotnet build .\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj --no-restore
dotnet run --project .\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj --no-build -- 3
```

The local restore source above prevents a feed lookup for this package-free console example. SDK installation and any restoration of the existing test packages are preparation, not part of the offline classroom run. A missing SDK or uncached test package must be prepared before the workshop.

Expected output for `3`:

```text
Offline teaching example: synthetic robot sports day quiz.
No model calls, network access, publication or real pupil data.

Before: 3/5
Feedback: Trace another pass and explain each swap to a partner.

After: 3/5
Feedback: Trace another pass and explain each swap to a partner.

Both examples delegate marks to ExactMatchGradingPolicy.
```

Omit the final score to use `3`. Use `0` or `5` to see the other feedback bands. A score outside `0` to `5`, non-numeric text or more than one argument returns exit code `2`. The score argument constructs a synthetic attempt; it is not a way to submit or change a real pupil's mark. Fixed identifiers and the fixed UTC date in the fixture are for repeatability, not live share tokens or a scheduled workshop start time.

## Proposed live-review diff

This comparison reads two real files and does not change the checkout. Git returns `1` when the files differ; that is the expected result, not a compilation failure.

```powershell
git --no-pager diff --no-index -- .\examples\GradingPolicyWorkshop\Before\QuizSession.cs .\examples\GradingPolicyWorkshop\After\QuizSession.cs
Get-Content .\examples\GradingPolicyWorkshop\After\SortingFeedbackPolicy.cs
Get-Content .\examples\GradingPolicyWorkshop\Program.cs
```

The namespace change lets both versions compile side by side. The important part of the actual diff is:

```diff
-namespace GradingPolicyWorkshop.Before;
+namespace GradingPolicyWorkshop.After;

 public sealed class QuizSession
 {
+    private readonly IGradingPolicy gradingPolicy;
+
+    public QuizSession(IGradingPolicy gradingPolicy)
+    {
+        ArgumentNullException.ThrowIfNull(gradingPolicy);
+        this.gradingPolicy = gradingPolicy;
+    }
+
     public QuizResult Submit(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt)
     {
-        var result = new ExactMatchGradingPolicy().Grade(quiz, attempt, submittedAt);
-        var feedback = result.Score switch
-        {
-            5 => "Explain why the sorted finish times still contain every robot.",
-            >= 3 => "Trace another pass and explain each swap to a partner.",
-            _ => "Build a trace table with a partner and compare neighbouring times."
-        };
-        return result with { Feedback = feedback };
+        return gradingPolicy.Grade(quiz, attempt, submittedAt);
     }
 }
```

The feedback switch has moved into `SortingFeedbackPolicy`; it has not vanished. The real, single-method interface remains in `src\LessonPlanner.Core\Boundaries.cs`:

```csharp
public interface IGradingPolicy
{
    QuizResult Grade(QuizContent quiz, QuizAttempt attempt, DateTimeOffset submittedAt);
}
```

What to notice:

1. **Objects and responsibilities:** `QuizSession` is a class. The program creates an object that handles a submission, rather than combining all work in the console entry point.
2. **Interface:** callers know what `Grade` accepts and returns, not how each class implements it. An interface is a contract, not a copy of an algorithm.
3. **Constructor dependency injection:** `Program.cs` supplies the policy. The `readonly` field makes that choice stable for the lifetime of the session. No service container or new framework is required to demonstrate DI.
4. **Explicit policy:** `SortingFeedbackPolicy` calls its injected marker first, then uses a record `with` expression to replace only `Feedback`. The marking result is not mutated.
5. **Unchanged grading:** `ExactMatchGradingPolicy` still awards one mark per exact option match and rejects invalid submissions. Five questions, answer keys, question explanations, score, total, identifiers and timestamp are not changed by the workshop policy. Feedback bands are teaching suggestions, not GCSE grade boundaries or pass/fail decisions.
6. **Testability:** a small recording policy proves the session uses the injected dependency once. The tests do not need a model, web server, database or mocking package.

The original `Before` version is deliberately safe and working. This is a change in responsibility and coupling, not a demonstration of bypassing validation. DI alone is not a security boundary; the actual marker still has to be chosen by trusted application code.

## Twenty-minute paired activity

Use three pairs. One teacher reads the code aloud or describes it; the other predicts and runs it. Swap roles before editing. All steps can be completed seated, with a keyboard or by discussion; no robot hardware or timed physical activity is needed.

- **3 minutes:** predict the score and feedback for `3`, then run the example.
- **5 minutes:** review the diff, extracted policy and composition lines. Identify the object, interface and constructor parameter.
- **5 minutes:** run the focused tests. Explain why incomplete submissions throw rather than receiving a low score.
- **7 minutes:** make the safe change below, rerun the tests and compare scores with feedback.

## Focused tests and repository integration

The maintainer has added the example to `LessonPlanner.slnx` and referenced it from `LessonPlanner.UnitTests`:

```xml
<ProjectReference Include="..\..\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj" />
```

The solution, test project manifest and lockfiles remain owned by the repository maintainer. Workshop participants do not need to add project references. With the repository's test dependencies restored, run:

```powershell
dotnet test .\tests\LessonPlanner.UnitTests\LessonPlanner.UnitTests.csproj --no-restore --filter FullyQualifiedName~GradingPolicyWorkshopExampleTests --logger "console;verbosity=minimal"
```

The example project copies the two JSON inputs into `lesson-inputs` beside the output assembly, including the referencing test project's output. Tests check all six possible scores, the feedback boundaries, dependency forwarding, preservation of marking data, invalid answers and identifiers, fixture shape and request JSON validation. They deliberately do not require publication or model access.

The command above was verified directly in the integrated `LessonPlanner.UnitTests` project: **28 passed, 0 failed, 0 skipped**. This includes reading and validating both lesson inputs from the test output directory. The earlier isolated in-repository validation harness has been removed.

The console build had no warnings or errors, and 11 command-line cases passed: scores `0` to `5`, the default and four invalid-argument cases. The exercise below was checked through its expected failing test and passing change, then the original policy and test expectation were restored and all 28 tests passed again.

## Safe modification exercise

Change when **feedback** offers extra scaffolding, not when an answer earns a mark:

1. In `FeedbackBandsAreExplicit`, change only the expected message for score `3` to `Build a trace table with a partner and compare neighbouring times.`
2. Run the focused test command. That score's feedback test should fail; the marking tests should still pass.
3. In `After\SortingFeedbackPolicy.cs`, change `>= 3` to `>= 4`. Leave `Before`, `ExactMatchGradingPolicy`, answer keys and totals alone.
4. Run the tests again. They should all pass.
5. Rebuild and run with `3` and `4`:

```powershell
dotnet run --project .\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj --no-restore -- 3
dotnet run --project .\examples\GradingPolicyWorkshop\GradingPolicyWorkshop.csproj --no-build -- 4
```

For `3`, the two versions now give different feedback but both still return `3/5`. For `4`, both use the middle feedback message. Discuss why a feedback threshold is not an assessment grade boundary. Restore the original threshold and test expectation when returning to the unmodified example.

## Lesson-request teaching inputs

Both files have exactly `Topic`, `EducationalLevel`, `DurationMinutes` and `Instructions`, matching `LessonRequest`. Local tests deserialise them using the real JSON options and validate them with `DomainRules.ValidateRequest`.

- **OCR J277 sorting:** `ocr-j277-sorting.json` requests a 60-minute GCSE lesson tracing bubble, insertion and merge sorts. It includes duplicate times, a pass-versus-complete-sort distinction and inclusive practice routes.
- **Robot sports day:** `robot-sports-day.json` requests a 50-minute Key Stage 3 lesson with fictional robots, duplicate times, seated alternatives and an optional explanation of interfaces. It does not imply that C# interfaces are an OCR J277 assessment requirement.

**Neither request has been tested against a live model.** Request-shape validation is not evidence of curriculum accuracy, lesson quality, accessibility or model safety. Teachers must review any later generated lesson before use. These durations describe pupil lessons, not the 90-minute teacher workshop.

## Production and checkpoint boundaries

The console fixture includes visible synthetic marking keys so teachers can inspect the example. Its source identifiers are deliberately empty: it claims no retrieved curriculum evidence and fails full publication validation. Do not publish this fixture or copy its attempt-generation helper into a pupil client. In the real application, private marking stays server-side and publication still requires the normal validation and approval workflow.

This directory does not register the workshop policy in production or alter production feedback, persistence, authentication or marking. The maintainer owns solution references, root application documentation and the reproducible checkpoint manifest. Add these example files and the focused test command to that manifest using real repository revisions and measured outcomes. This example invents no checkpoints or commit hashes.
