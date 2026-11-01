using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using LessonPlanner.Core;

namespace LessonPlanner.Agents;

internal sealed record SortingWorkedExample(string Algorithm, ImmutableArray<int> Input,
    ImmutableArray<string> Steps, ImmutableArray<int> Result);

internal sealed record SortingExampleSet(string Provenance, ImmutableArray<SortingWorkedExample> Examples);

internal static class SortingWorkedExamples
{
    private static readonly Regex Topic = new(
        @"^\s*(?:(?:the\s+)?trac(?:e|ing)\s+|sorting\s+algorithms\s*:\s*)bubble\s*,\s*insertion\s*,?\s+and\s+merge\s+sort\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ListDeclaration = new(@"\bexample\s+list\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DeclaredValues = new(
        @"^\s*[:=]?\s*(?:\[(?<values>[^\[\]\r\n]*)\]|(?<values>[+-]?[0-9]+(?:(?:\s*,\s*(?:and\s+)?|\s+and\s+)[+-]?[0-9]+)+)(?=\s*(?:to\b|and\b|[.;\r\n]|$)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex BracketedValues = new(@"\[(?<values>[^\[\]\r\n]*[0-9][^\[\]\r\n]*)\]",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ValueSeparator = new(@"\s*,\s*(?:and\s+)?|\s+and\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex NumericContinuation = new(
        @"^\s*(?:\.[0-9]|" +
        @"(?:,\s*(?:and\s+)?|and\s+|(?:,\s*)?or\s+(?:(?:alternatively|instead)\s+)?(?:(?:use|choose|try|consider|take)\s+)?(?:(?:a|an|the|another)\s+)?(?:(?:different|alternative|second|other|example)\s+)*(?:(?:list|sequence|numbers|values|set)(?:\s+of)?\s*)?)" +
        @"(?:[+\-0-9.\[]|(?:zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|minus|plus|negative|positive|half|quarter)\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool Applies(LessonRequest request)
    {
        if (!string.Equals(request.EducationalLevel.Trim(), "GCSE (OCR J277)", StringComparison.OrdinalIgnoreCase))
            return false;
        return Topic.IsMatch(request.Topic);
    }

    internal static SortingExampleSet? For(LessonRequest request, ImmutableArray<SourceReference> sources)
    {
        if (!Applies(request) || SortingTeachingSources.Required.Any(expected => !sources.Any(source =>
                source.Id == expected.Id && source.Title == expected.Title && source.Url == expected.Url)))
            return null;
        var input = Input(request.Instructions);
        if (input is null) return null;
        return new("Application-calculated examples. The procedure descriptions come from the supplied sources.",
            [Bubble(input), Insertion(input), Merge(input)]);
    }

    internal static void ValidateInput(LessonRequest request)
    {
        if (Applies(request)) _ = Input(request.Instructions);
    }

    private static int[]? Input(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return [5, 3, 4, 1];
        var declarations = ListDeclaration.Matches(instructions);
        Match values;
        var offset = 0;
        if (declarations.Count > 1) throw InvalidList();
        if (declarations.Count == 1)
        {
            var declaration = declarations[0];
            offset = declaration.Index + declaration.Length;
            values = DeclaredValues.Match(instructions[offset..]);
            if (!values.Success) throw InvalidList();
        }
        else
        {
            var brackets = BracketedValues.Matches(instructions);
            if (brackets.Count == 0) return null;
            if (brackets.Count > 1) throw InvalidList();
            values = brackets[0];
        }
        if (NumericContinuation.IsMatch(instructions[(offset + values.Index + values.Length)..]))
            throw InvalidList();
        var items = ValueSeparator.Split(values.Groups["values"].Value);
        if (items.Length is < 2 or > 8) throw InvalidList();
        var input = new int[items.Length];
        for (var index = 0; index < items.Length; index++)
            if (!int.TryParse(items[index].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out input[index]))
                throw InvalidList();
        return input;
    }

    private static DomainException InvalidList() =>
        new("sorting_example", "Use one example list with two to eight whole numbers separated by commas or 'and'.");

    private static SortingWorkedExample Bubble(int[] input)
    {
        var values = (int[])input.Clone();
        var steps = ImmutableArray.CreateBuilder<string>();
        var pass = 0;
        bool swapped;
        do
        {
            swapped = false;
            pass++;
            for (var index = 0; index < values.Length - 1; index++)
            {
                var left = values[index];
                var right = values[index + 1];
                var swap = left > right;
                if (swap)
                {
                    (values[index], values[index + 1]) = (right, left);
                    swapped = true;
                }
                steps.Add($"Pass {pass}: compare {left} and {right}; {(swap ? "swap" : "leave in place")}: {List(values)}.");
            }
        } while (swapped);
        steps.Add($"Pass {pass} made no swaps, so the list is sorted.");
        return new("Bubble sort", input.ToImmutableArray(), steps.ToImmutable(), values.ToImmutableArray());
    }

    private static SortingWorkedExample Insertion(int[] input)
    {
        var values = (int[])input.Clone();
        var steps = ImmutableArray.CreateBuilder<string>();
        steps.Add($"Start with the sorted sublist [{values[0]}].");
        for (var index = 1; index < values.Length; index++)
        {
            var value = values[index];
            var position = index - 1;
            var comparisons = new List<string>();
            while (position >= 0 && values[position] > value)
            {
                comparisons.Add($"{values[position]} > {value}, shift right");
                values[position + 1] = values[position];
                position--;
            }
            if (position >= 0) comparisons.Add($"{values[position]} <= {value}, stop");
            values[position + 1] = value;
            steps.Add($"Insert {value}: {string.Join("; ", comparisons)}; {List(values)}.");
        }
        return new("Insertion sort", input.ToImmutableArray(), steps.ToImmutable(), values.ToImmutableArray());
    }

    private static SortingWorkedExample Merge(int[] input)
    {
        var steps = ImmutableArray.CreateBuilder<string>();
        int[] Sort(int[] values)
        {
            if (values.Length < 2) return values;
            var middle = values.Length / 2;
            var leftInput = values[..middle];
            var rightInput = values[middle..];
            steps.Add($"Split {List(values)} into {List(leftInput)} and {List(rightInput)}.");
            var left = Sort(leftInput);
            var right = Sort(rightInput);
            var merged = new List<int>(values.Length);
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftValue = left[leftIndex];
                var rightValue = right[rightIndex];
                var takeLeft = leftValue <= rightValue;
                var chosen = takeLeft ? left[leftIndex++] : right[rightIndex++];
                merged.Add(chosen);
                steps.Add($"Compare {leftValue} and {rightValue}; take {chosen}: {List(merged)}.");
            }
            while (leftIndex < left.Length) merged.Add(left[leftIndex++]);
            while (rightIndex < right.Length) merged.Add(right[rightIndex++]);
            steps.Add($"Merge {List(left)} and {List(right)}: {List(merged)}.");
            return merged.ToArray();
        }
        var result = Sort((int[])input.Clone());
        return new("Merge sort", input.ToImmutableArray(), steps.ToImmutable(), result.ToImmutableArray());
    }

    private static string List(IEnumerable<int> values) => $"[{string.Join(", ", values)}]";
}
