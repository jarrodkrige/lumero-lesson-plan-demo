using System.Globalization;
using System.Text;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

internal static class CurriculumText
{
    internal static string ValidateAndNormalize(string text, CatalogueEntry entry, CurriculumRetrievalOptions options,
        CancellationToken cancellationToken)
    {
        if (text.Length > options.MaximumTextCharacters)
            throw new DomainException("source_text_limit", "The curriculum text exceeds the extraction budget.");

        var normalized = new StringBuilder(text.Length);
        var pendingSpace = false;
        var pendingNewline = false;
        var lettersAndDigits = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rune.Value == 0x00AD) continue;
            if (rune == Rune.ReplacementChar || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ||
                (Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t' or '\f')))
                throw new DomainException("source_invalid_text", "The curriculum document contains invalid or unsupported text.");
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = normalized.Length > 0;
                pendingNewline |= rune.Value is '\r' or '\n' or '\f';
                continue;
            }
            if (pendingSpace) normalized.Append(pendingNewline ? '\n' : ' ');
            pendingSpace = pendingNewline = false;
            normalized.Append(rune.ToString());
            if (Rune.IsLetterOrDigit(rune)) lettersAndDigits++;
        }

        var result = normalized.ToString();
        var searchable = result.Replace('\n', ' ');
        if (result.Length < 100 || lettersAndDigits < 40 ||
            entry.IdentityMarkers.Any(marker => !searchable.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("source_invalid_text", "The response does not contain the expected curriculum text.");
        return result;
    }

    internal static string ShortExtract(string text, string anchor, int maximumCharacters)
    {
        var singleLine = text.Replace('\n', ' ');
        var anchorIndex = singleLine.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, anchorIndex - 80);
        if (char.IsLowSurrogate(singleLine[start])) start++;
        var length = Math.Min(maximumCharacters, singleLine.Length - start);
        if (char.IsHighSurrogate(singleLine[start + length - 1])) length--;
        return singleLine.Substring(start, length).Trim();
    }
}
