using System.Text;
using System.Text.RegularExpressions;

namespace Tman.Core;

/// <param name="Text">The tidied text.</param>
/// <param name="Problems">Things a tidy-up can't settle, each with its line in the tidied text.</param>
public sealed record FormatResult(string Text, List<string> Problems);

/// <summary>
/// Lays out a Compose file the way Docker's own examples are: two spaces a level, lists indented under their key, one blank
/// line between top-level sections and between services. It works line by line and only ever changes whitespace, so comments
/// stay, values are never rewritten, and what Compose reads from the file is the same before and after.
/// </summary>
public static class ComposeFormat
{
    const int Step = 2;

    sealed class Level
    {
        public int Old, New;
        /// <summary>A list written level with its key, which gets indented under it.</summary>
        public bool FlushList;
        /// <summary>The last line here was a key with nothing after it, so what follows belongs to it.</summary>
        public bool OpenKey;
        public HashSet<string> Keys = new(StringComparer.Ordinal);
    }

    sealed record Line(int Indent, string Text, bool Blank, bool Comment);

    static readonly Regex BlockHeader = new(@"(^|[\s:\-])[|>][+\-]?[1-9]?[+\-]?\s*(#.*)?$", RegexOptions.Compiled);
    static readonly Regex PlainKey = new(@"^(?<dash>(-\s+)*)(?<key>[A-Za-z0-9_.\-/]+|""[^""]*""|'[^']*'):(?<gap>\s*)(?<rest>.*)$", RegexOptions.Compiled);
    static readonly Regex DashGap = new(@"^-\s{2,}(?=\S)", RegexOptions.Compiled);

    public static FormatResult Yaml(string source)
    {
        var problems = new List<string>();
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n').Select(Read).ToList();
        var output = new List<(int Indent, string Text)>();
        var stack = new List<Level> { new() { Old = -1, New = -Step } };

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Blank)
            {
                output.Add((0, ""));
                continue;
            }
            if (line.Comment)
            {
                // A comment sits with whatever comes after it.
                var next = lines.Skip(i + 1).FirstOrDefault(l => !l.Blank && !l.Comment);
                output.Add((next == null ? Place(stack, line.Indent, false, null) : -1, line.Text));
                continue;
            }

            bool dash = line.Text == "-" || line.Text.StartsWith("- ");
            int indent = Place(stack, line.Indent, dash, problem => problems.Add($"Line {output.Count + 1}: {problem}"));
            var text = Tidy(line.Text);
            var level = stack[^1];
            Note(level, text, output.Count + 1, problems);
            output.Add((indent, text));

            // Text that runs on over several lines moves with its first line and is otherwise left exactly as it is.
            int more = Continuation(lines, i, text);
            for (int j = i + 1; j <= more; j++)
                output.Add(lines[j].Blank ? (0, "") : (Math.Max(0, lines[j].Indent - line.Indent + indent), lines[j].Text));
            if (more > i && !BlockHeader.IsMatch(StripComment(text)) && Open(string.Join("\n", lines.Skip(i).Take(more - i + 1).Select(l => l.Text))) != 0)
                problems.Add($"Line {output.Count}: a bracket or quote is never closed.");
            i = more;
        }

        // Comments take the indentation of the line they introduce.
        for (int i = output.Count - 1, below = 0; i >= 0; i--)
        {
            if (output[i].Indent == -1) output[i] = (below, output[i].Text);
            else if (output[i].Text.Length > 0) below = output[i].Indent;
        }

        return new FormatResult(Join(Space(output)), problems);
    }

    static Line Read(string raw)
    {
        // Tabs can't indent YAML at all. Each becomes one level.
        int i = 0, indent = 0;
        for (; i < raw.Length && (raw[i] == ' ' || raw[i] == '\t'); i++) indent += raw[i] == '\t' ? Step : 1;
        var text = raw[i..].TrimEnd();
        return new Line(indent, text, text.Length == 0, text.StartsWith('#'));
    }

    /// <summary>Finds the level a line belongs to, opening or closing levels as its indentation asks, and returns its new indentation.</summary>
    static int Place(List<Level> stack, int old, bool dash, Action<string>? problem)
    {
        bool closed = false;
        while (stack[^1].Old > old)
        {
            stack.RemoveAt(stack.Count - 1);
            closed = true;
        }
        var top = stack[^1];
        // A list written level with its key ends when a key comes back at that indentation.
        if (top.Old == old && top.FlushList && !dash)
        {
            stack.RemoveAt(stack.Count - 1);
            top = stack[^1];
        }
        if (top.Old == old)
        {
            if (!dash || !top.OpenKey) return top.New;
            var list = new Level { Old = old, New = top.New + Step, FlushList = true };
            stack.Add(list);
            return list.New;
        }
        if (closed && problem != null) problem("its indentation doesn't line up with anything above it. Check which key it belongs under.");
        var level = new Level { Old = old, New = top.New + Step };
        stack.Add(level);
        return level.New;
    }

    /// <summary>Remembers what this line opens, and spots a key written twice.</summary>
    static void Note(Level level, string text, int number, List<string> problems)
    {
        var match = PlainKey.Match(text);
        if (!match.Success)
        {
            level.OpenKey = false;
            return;
        }
        bool inList = match.Groups["dash"].Length > 0;
        var rest = StripComment(match.Groups["rest"].Value).Trim();
        // "- name:" starts a new item, whose keys are counted afresh on the lines below it.
        level.OpenKey = rest.Length == 0 && !inList;
        if (inList) return;
        if (!level.Keys.Add(match.Groups["key"].Value))
            problems.Add($"Line {number}: \"{match.Groups["key"].Value}\" appears twice here. Compose refuses a file with a repeated key.");
    }

    /// <summary>One space after a list dash and after a key's colon.</summary>
    static string Tidy(string text)
    {
        text = DashGap.Replace(text, "- ");
        var match = PlainKey.Match(text);
        if (!match.Success || match.Groups["gap"].Length <= 1 || match.Groups["rest"].Length == 0) return text;
        return $"{match.Groups["dash"].Value}{match.Groups["key"].Value}: {match.Groups["rest"].Value}";
    }

    /// <summary>The last line that is still part of the value starting on line <paramref name="start"/>.</summary>
    static int Continuation(List<Line> lines, int start, string text)
    {
        int last = start;
        if (BlockHeader.IsMatch(StripComment(text)))
        {
            // A block of literal text: every deeper line, and blank lines between them.
            for (int j = start + 1; j < lines.Count; j++)
            {
                if (lines[j].Blank) continue;
                if (lines[j].Indent <= lines[start].Indent) break;
                last = j;
            }
            return last;
        }
        // Brackets or a quote left open carry on until they close.
        var joined = new StringBuilder(lines[start].Text);
        while (Open(joined.ToString()) != 0 && last + 1 < lines.Count)
        {
            last++;
            joined.Append('\n').Append(lines[last].Text);
        }
        return last;
    }

    /// <summary>How many brackets are still open, or -1 inside an unclosed quote. Comments and quoted text don't count.</summary>
    static int Open(string text)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            bool startsToken = i == 0 || " \t\n[{,:-".Contains(text[i - 1]);
            if ((c == '"' || c == '\'') && startsToken) quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c is '[' or '{' && (depth > 0 || startsToken)) depth++;
            else if (c is ']' or '}' && depth > 0) depth--;
        }
        return quote != '\0' ? -1 : depth;
    }

    static string StripComment(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if ((c == '"' || c == '\'') && (i == 0 || " \t[{,:-".Contains(text[i - 1]))) quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) return text[..i];
        }
        return text;
    }

    /// <summary>
    /// Sets the blank lines: none at the start or doubled up, one between top-level sections, and one between the entries of
    /// a section when they run to several lines each, as services do.
    /// </summary>
    static List<(int Indent, string Text)> Space(List<(int Indent, string Text)> lines)
    {
        var kept = lines.Where((l, i) => l.Text.Length > 0 || (i > 0 && lines[i - 1].Text.Length > 0)).ToList();
        while (kept.Count > 0 && kept[^1].Text.Length == 0) kept.RemoveAt(kept.Count - 1);

        // An entry starts at its first comment, so the gap goes above the comment.
        bool Starts(int i, int indent) =>
            kept[i].Indent == indent && kept[i].Text.Length > 0 && (i == 0 || kept[i - 1].Text.Length == 0 || !(kept[i - 1].Indent == indent && kept[i - 1].Text.StartsWith('#')));
        int Size(int i, int indent)
        {
            // Lines of the entry's own body. Blank lines and the next entry's comments aren't part of it.
            int n = 1;
            for (int j = i + 1; j < kept.Count; j++)
            {
                if (kept[j].Text.Length == 0) continue;
                if (kept[j].Indent <= indent) break;
                n++;
            }
            return n;
        }

        var result = new List<(int, string)>();
        for (int i = 0; i < kept.Count; i++)
        {
            var (indent, text) = kept[i];
            bool wantGap = text.Length > 0 && result.Count > 0 && result[^1].Item2.Length > 0 && indent <= Step && Starts(i, indent)
                && (indent == 0 || MultiLineSiblings(kept, i, indent, Size));
            if (wantGap) result.Add((0, ""));
            result.Add((indent, text));
        }
        return result;
    }

    /// <summary>True when this second-level entry or the one before it runs to more than one line.</summary>
    static bool MultiLineSiblings(List<(int Indent, string Text)> lines, int i, int indent, Func<int, int, int> size)
    {
        if (lines[i].Text.StartsWith("- ") || lines[i].Text == "-") return false;
        int previous = -1;
        for (int j = i - 1; j >= 0; j--)
        {
            if (lines[j].Text.Length == 0 || lines[j].Text.StartsWith('#')) continue;
            if (lines[j].Indent < indent) break;
            if (lines[j].Indent == indent)
            {
                previous = j;
                break;
            }
        }
        if (previous < 0) return false;
        return size(previous, indent) > 1 || size(i, indent) > 1;
    }

    static string Join(List<(int Indent, string Text)> lines)
    {
        var text = new StringBuilder();
        foreach (var (indent, line) in lines)
        {
            if (line.Length > 0) text.Append(' ', indent).Append(line);
            text.Append(Environment.NewLine);
        }
        return text.ToString();
    }

    /// <summary>Tidies a settings file of NAME=value lines: no stray spaces round the name, no doubled blank lines.</summary>
    public static FormatResult Env(string source)
    {
        var problems = new List<string>();
        var output = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                continue;
            }
            if (line.StartsWith('#'))
            {
                output.Add(line);
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                output.Add(line);
                problems.Add($"Line {output.Count}: \"{Short(line)}\" has no = sign, so it sets nothing.");
                continue;
            }
            var name = line[..equals].Trim();
            // The value is kept as typed: spaces inside it can matter.
            var value = line[(equals + 1)..].TrimStart();
            output.Add($"{name}={value}");
            if (!Regex.IsMatch(name.StartsWith("export ") ? name[7..].Trim() : name, @"^[A-Za-z_][A-Za-z0-9_.\-]*$"))
                problems.Add($"Line {output.Count}: \"{Short(name)}\" isn't a valid name.");
            else if (!names.Add(name))
                problems.Add($"Line {output.Count}: {name} is set twice. The last one wins.");
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return new FormatResult(output.Count == 0 ? "" : string.Join(Environment.NewLine, output) + Environment.NewLine, problems);
    }

    static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
