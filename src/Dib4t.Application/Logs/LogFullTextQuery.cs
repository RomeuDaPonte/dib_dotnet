using System.Text;

namespace Dib4t.Application.Logs;

/// <summary>
/// The Registos search box → an FTS5 query (PLAN.md §3.3: prefix search, quoted phrases). Every term becomes a quoted
/// FTS5 string, so no input is FTS5 syntax (no operators, columns or syntax errors): a bare word is a prefix
/// (<c>restor</c> → <c>"restor"*</c>, a trailing <c>*</c> is accepted), <c>"…"</c> is an exact phrase (an unclosed quote
/// runs to the end). Terms are all required (AND). Terms without a letter or digit are ignored (the tokenizer would
/// drop them anyway).
/// </summary>
public static class LogFullTextQuery
{
    /// <summary>The FTS5 query, or <see langword="null"/> when the text has no searchable term.</summary>
    public static string? Translate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                var phrase = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                i = end < 0 ? text.Length : end + 1;
                if (Searchable(phrase))
                {
                    terms.Add(Quote(phrase.Trim()));
                }
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"')
                {
                    i++;
                }

                var word = text[start..i].TrimEnd('*');
                if (Searchable(word))
                {
                    terms.Add(Quote(word) + "*");
                }
            }
        }

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    private static bool Searchable(string term)
    {
        foreach (var c in term)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string Quote(string term) => new StringBuilder(term.Length + 2).Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"').ToString();
}
