namespace Blueline.Core;

/// <summary>
/// Matching for the Ctrl+K palette. Every word you type has to appear somewhere in the text, in any order, ignoring case.
/// Matches that start the text, or start a word in it, rank above matches in the middle of a word.
/// </summary>
public static class PaletteSearch
{
    /// <summary>0 when <paramref name="text"/> does not match, otherwise higher means a better match.</summary>
    public static int Score(string query, string text)
    {
        var words = Words(query);
        if (words.Length == 0)
            return 1;

        var total = 0;
        foreach (var word in words)
        {
            var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return 0;
            total += at == 0 ? 100 : StartsWord(text, at) ? 60 : 10;
        }

        // A shorter text that matches is closer to what was typed than a long path that happens to contain it.
        return total + Math.Max(0, 30 - text.Length / 6);
    }

    public static string[] Words(string query) => query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWord(string text, int at) => at > 0 && !char.IsLetterOrDigit(text[at - 1]);
}
