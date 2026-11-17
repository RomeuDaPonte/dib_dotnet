using System.Text;
using System.Text.Json;

namespace Dib4t.Tests.Common;

/// <summary>
/// One effective NAS operation with absolute paths: the contract of <c>tests/fixtures/legacy/ssh/ssh-commands.json</c>
/// (<c>ops</c>; its <c>result</c> is left out, the folder tree shows it).
/// </summary>
public sealed record NasOp(string Op, string? Path = null, string? From = null, string? To = null)
{
    public static NasOp MakeDirectory(string path) => new("mkdir-p", path);

    public static NasOp TestDirectory(string path) => new("test-d", path);

    public static NasOp Move(string from, string to) => new("mv", From: from, To: to);

    /// <summary>An <c>ops</c> entry of the golden file.</summary>
    public static NasOp FromGolden(JsonElement op) => new(
        op.GetProperty("op").GetString()!,
        op.TryGetProperty("path", out var path) ? path.GetString() : null,
        op.TryGetProperty("from", out var from) ? from.GetString() : null,
        op.TryGetProperty("to", out var to) ? to.GetString() : null);

    /// <summary>
    /// The operations of a command <b>the new app</b> sends: <c>mkdir -p W</c>, <c>if test -d W; then echo 1; else
    /// echo 0; fi</c> or <c>mv W W</c>, where every path is one fully single-quoted <c>sh</c> word (<c>'\''</c> for a
    /// quote). Anything else (an unquoted path, an expansion, another command) throws: the adapter must not send it.
    /// Relative paths would resolve against the SSH user's home (<see cref="NasFake.Home"/>).
    /// </summary>
    public static IReadOnlyList<NasOp> FromCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var words = Split(command);
        string Path(int index) => words[index] is { Quoted: true } word ? Absolute(word.Text) : throw Unexpected(command);

        if (words.Count == 3 && words[0] is { Text: "mkdir", Quoted: false } && words[1] is { Text: "-p", Quoted: false })
        {
            return [MakeDirectory(Path(2))];
        }

        if (words.Count == 3 && words[0] is { Text: "mv", Quoted: false })
        {
            return [Move(Path(1), Path(2))];
        }

        if (string.Join(' ', words.Select(w => w.Quoted ? "W" : w.Text)) == "if test -d W ; then echo 1 ; else echo 0 ; fi")
        {
            return [TestDirectory(Path(3))];
        }

        throw Unexpected(command);
    }

    private static string Absolute(string path) => path.StartsWith('/') ? path : NasFake.Home + "/" + path;

    private static InvalidOperationException Unexpected(string command) => new($"Not a command the NAS adapter may send: {command}");

    /// <summary>
    /// <c>sh</c> word splitting for the subset above: blanks separate words and <c>;</c> is a word of its own; a word is
    /// either fully unquoted (no quotes, <c>$</c>, backticks, globs or backslashes) or a run of single-quoted parts
    /// joined by <c>\'</c>.
    /// </summary>
    private static List<(string Text, bool Quoted)> Split(string command)
    {
        var words = new List<(string, bool)>();
        var i = 0;
        while (i < command.Length)
        {
            if (command[i] == ' ')
            {
                i++;
            }
            else if (command[i] == ';')
            {
                words.Add((";", false));
                i++;
            }
            else if (command[i] == '\'')
            {
                var text = new StringBuilder();
                while (i < command.Length && command[i] is not (' ' or ';'))
                {
                    if (command[i] == '\'')
                    {
                        var end = command.IndexOf('\'', i + 1);
                        if (end < 0)
                        {
                            throw new InvalidOperationException($"Unterminated quote: {command}");
                        }

                        text.Append(command, i + 1, end - i - 1);
                        i = end + 1;
                    }
                    else if (command[i] == '\\' && i + 1 < command.Length && command[i + 1] == '\'')
                    {
                        text.Append('\'');
                        i += 2;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unquoted text in a quoted word: {command}");
                    }
                }

                words.Add((text.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < command.Length && command[i] is not (' ' or ';'))
                {
                    if ("'\"\\$`*?[]~(){}<>|&#\n".Contains(command[i], StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Shell syntax outside quotes: {command}");
                    }

                    i++;
                }

                words.Add((command[start..i], false));
            }
        }

        return words;
    }
}
