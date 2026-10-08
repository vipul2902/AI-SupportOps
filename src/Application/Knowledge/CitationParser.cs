using System.Globalization;
using System.Text.RegularExpressions;

namespace AISupportOps.Application.Knowledge;

public sealed record CitationAnalysis(IReadOnlyList<ContextSource> Cited, IReadOnlyList<int> InvalidNumbers);

/// <summary>
/// Maps the model's inline [n] markers back to the sources actually in the prompt. Numbers that
/// do not correspond to a provided source are reported: a cheap but useful hallucination signal.
/// </summary>
public static partial class CitationParser
{
    public static CitationAnalysis Analyze(string answer, IReadOnlyList<ContextSource> sources)
    {
        var byNumber = sources.ToDictionary(s => s.Number);
        var cited = new List<ContextSource>();
        var invalid = new List<int>();

        foreach (Match match in CitationMarker().Matches(answer))
        {
            if (!int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (byNumber.TryGetValue(number, out var source))
            {
                if (!cited.Contains(source))
                {
                    cited.Add(source);
                }
            }
            else if (!invalid.Contains(number))
            {
                invalid.Add(number);
            }
        }

        return new CitationAnalysis(cited, invalid);
    }

    [GeneratedRegex(@"\[(?<n>\d{1,3})\]")]
    private static partial Regex CitationMarker();
}
