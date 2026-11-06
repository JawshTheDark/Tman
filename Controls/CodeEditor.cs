using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Tman.Controls;

public enum CodeLanguage { Yaml, Env }

/// <summary>
/// A text box for files where layout carries meaning: line numbers, coloured keys, values and comments, and a fixed-width
/// font. Tman's text is Tahoma everywhere else, but YAML nests by indentation, and that can't be read in a font where
/// every character has a different width.
/// </summary>
public sealed class CodeEditor : Border
{
    readonly TextEditor _editor = new();

    public event Action<string>? TextChanged;

    public CodeEditor(CodeLanguage language, string name)
    {
        Background = Solid("#0C0E12");
        BorderBrush = (Brush)Application.Current.FindResource("CardStrokeBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(0, 4, 0, 0);

        _editor.FontFamily = new FontFamily("Consolas");
        _editor.FontSize = 12.5;
        _editor.Background = Brushes.Transparent;
        _editor.Foreground = Solid("#DCE3EA");
        _editor.ShowLineNumbers = true;
        _editor.LineNumbersForeground = Solid("#4A5563");
        _editor.WordWrap = false;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // YAML can't be indented with tabs, so the Tab key types spaces.
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.TextArea.SelectionBrush = Solid("#553FC8FF");
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.SelectionCornerRadius = 2;
        _editor.TextArea.Caret.CaretBrush = Solid("#F2F5F8");
        _editor.TextArea.TextView.Margin = new Thickness(6, 0, 0, 0);
        _editor.TextArea.TextView.LineTransformers.Add(new Colours(language));
        AutomationProperties.SetName(_editor.TextArea, name);
        _editor.TextChanged += (_, _) => TextChanged?.Invoke(_editor.Text);
        Child = _editor;
    }

    static SolidColorBrush Solid(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    /// <summary>Setting this starts the undo history afresh, so it is for loading a file.</summary>
    public string Text
    {
        get => _editor.Text;
        set => _editor.Text = value;
    }

    /// <summary>Swaps the whole text as one edit, so Ctrl+Z brings the old text back, and keeps the caret on its line.</summary>
    public void Replace(string text)
    {
        int line = _editor.TextArea.Caret.Line;
        _editor.Document.Text = text;
        _editor.TextArea.Caret.Line = Math.Min(line, _editor.Document.LineCount);
        _editor.TextArea.Caret.Column = 1;
    }

    /// <summary>Colours one line at a time. Each line is read alone, which is all YAML and settings files need.</summary>
    sealed class Colours : DocumentColorizingTransformer
    {
        static readonly Brush Key = Solid("#33B5FF"), Text = Solid("#3DDC84"), Value = Solid("#B87CFF"), Variable = Solid("#FFD34D"),
            Mark = Solid("#7FA8C4"), Reference = Solid("#FFAD33"), Comment = Solid("#6F7A88");

        static readonly Regex YamlKey = new(@"^(?<lead>\s*(?:-\s+)*)(?<key>""[^""]*""|'[^']*'|[^\s#:\-][^:#]*?):(?=\s|$)", RegexOptions.Compiled);
        static readonly Regex Dashes = new(@"^\s*((?:-\s+)+|-$)", RegexOptions.Compiled);
        static readonly Regex Quoted = new(@"(?<=^|[\s\[{,:\-])(""(?:[^""\\]|\\.)*""|'[^']*')", RegexOptions.Compiled);
        static readonly Regex Variables = new(@"\$\{[^}\n]*\}|\$[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        static readonly Regex Constant = new(@"(?<=:\s+|-\s+)(true|false|yes|no|on|off|null|~|-?\d+(\.\d+)?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex References = new(@"(?<=^|[\s:\-])([&*][A-Za-z0-9_.\-]+|!!?[A-Za-z0-9_.\-]+|<<)(?=\s|:|$)", RegexOptions.Compiled);
        static readonly Regex BlockMark = new(@"(?<=:\s+|-\s+)[|>][+\-]?\d?\s*$", RegexOptions.Compiled);
        static readonly Regex EnvLine = new(@"^\s*(?<name>(?:export\s+)?[^=#\s][^=]*?)\s*(?<equals>=)", RegexOptions.Compiled);

        readonly CodeLanguage _language;

        public Colours(CodeLanguage language) => _language = language;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var text = CurrentContext.Document.GetText(line);
            int start = line.Offset;
            void Paint(int from, int length, Brush brush)
            {
                if (length > 0) ChangeLinePart(start + from, start + from + length, e => e.TextRunProperties.SetForegroundBrush(brush));
            }

            int comment = CommentStart(text);
            var code = comment >= 0 ? text[..comment] : text;
            if (_language == CodeLanguage.Env)
            {
                if (EnvLine.Match(code) is { Success: true } env)
                {
                    Paint(env.Groups["name"].Index, env.Groups["name"].Length, Key);
                    Paint(env.Groups["equals"].Index, 1, Mark);
                }
            }
            else
            {
                if (Dashes.Match(code) is { Success: true } dashes) Paint(dashes.Groups[1].Index, dashes.Groups[1].Length, Mark);
                if (Constant.Match(code) is { Success: true } constant) Paint(constant.Groups[1].Index, constant.Groups[1].Length, Value);
                if (BlockMark.Match(code) is { Success: true } block) Paint(block.Index, block.Length, Mark);
                foreach (Match m in References.Matches(code)) Paint(m.Groups[1].Index, m.Groups[1].Length, Reference);
                foreach (Match m in Quoted.Matches(code)) Paint(m.Groups[1].Index, m.Groups[1].Length, Text);
                if (YamlKey.Match(code) is { Success: true } key)
                {
                    Paint(key.Groups["key"].Index, key.Groups["key"].Length, Key);
                    Paint(key.Groups["key"].Index + key.Groups["key"].Length, 1, Mark);
                }
            }
            // Painted last, so a ${NAME} inside quotes still stands out.
            foreach (Match m in Variables.Matches(code)) Paint(m.Index, m.Length, Variable);
            if (comment >= 0) Paint(comment, text.Length - comment, Comment);
        }

        /// <summary>Where a comment begins: a # at the start or after a space, outside quotes. -1 if there isn't one.</summary>
        static int CommentStart(string text)
        {
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"') i++;
                    else if (c == quote) quote = '\0';
                }
                else if ((c == '"' || c == '\'') && (i == 0 || " \t[{,:-=".Contains(text[i - 1]))) quote = c;
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) return i;
            }
            return -1;
        }
    }
}
