using System.Text;
using System.Text.RegularExpressions;

namespace AISupportOps.Application.Ingestion;

/// <summary>
/// Cleans extracted text so that equivalent content produces equivalent tokens/embeddings:
/// Unicode compatibility normalization, consistent newlines, no control characters,
/// PDF line-break hyphenation repaired, and whitespace collapsed (paragraph breaks preserved).
/// </summary>
public static partial class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // NFKC folds look-alike characters (ligatures "ﬁ" → "fi", full-width digits, etc.).
        var normalized = text.Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        normalized = StripControlCharacters(normalized);
        normalized = HyphenatedLineBreak().Replace(normalized, "$1$2");
        normalized = HorizontalWhitespace().Replace(normalized, " ");
        normalized = SpaceAroundNewline().Replace(normalized, "\n");
        normalized = ExcessNewlines().Replace(normalized, "\n\n");

        return normalized.Trim();
    }

    private static string StripControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
            {
                builder.Append(c is ' ' ? ' ' : c);
            }
        }

        return builder.ToString();
    }

    // "infor-\nmation" -> "information" (only lowercase continuation, to keep "e-\nMail" style compounds intact-ish).
    [GeneratedRegex(@"(\p{L})-\n(\p{Ll})")]
    private static partial Regex HyphenatedLineBreak();

    [GeneratedRegex(@"[ \t\f\v -   　]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessNewlines();
}
