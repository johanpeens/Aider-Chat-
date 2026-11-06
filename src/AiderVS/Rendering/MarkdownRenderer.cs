using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AiderVS
{
    /// <summary>
    /// Turns the Markdown a model writes (headings, **bold**, *italic*, `code`, lists, quotes, rules, links)
    /// into a FlowDocument. Fenced code blocks are handled elsewhere; this only sees the text between them.
    /// A single line break is kept as a line break, unless the line was clearly wrapped by a console.
    /// </summary>
    public static class MarkdownRenderer
    {
        static readonly Regex Heading = new Regex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        static readonly Regex Bullet = new Regex(@"^(\s*)([-*+•])\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Numbered = new Regex(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Quote = new Regex(@"^\s*>\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex Rule = new Regex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);

        // `code`, **bold**, __bold__, *italic*, _italic_, [text](url)
        static readonly Regex Inline = new Regex(
            @"(?<tick>`+)(?<ctext>.+?)\k<tick>" +
            @"|\*\*(?<b1>[^\s*](?:.*?[^\s*])?)\*\*" +
            @"|__(?<b2>[^\s_](?:.*?[^\s_])?)__" +
            @"|(?<![\*\w])\*(?<i1>[^\s*](?:.*?[^\s*])?)\*(?![\*\w])" +
            @"|(?<![\w_])_(?<i2>[^\s_](?:.*?[^\s_])?)_(?![\w_])" +
            @"|\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\)",
            RegexOptions.Compiled);

        static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
        static readonly Brush GreyLine = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80)));

        public static void Render(FlowDocument document, string text, Brush linkBrush)
        {
            document.Blocks.Clear();
            document.PagePadding = new Thickness(0);

            Paragraph running = null;   // the text paragraph that following plain lines are added to
            string previousLine = null;

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Trim().Length == 0)
                {
                    running = null;
                    continue;
                }

                var m = Heading.Match(raw);
                if (m.Success)
                {
                    running = null;
                    document.Blocks.Add(MakeHeading(m.Groups[1].Length, m.Groups[2].Value, linkBrush));
                    continue;
                }

                if (Rule.IsMatch(raw))
                {
                    running = null;
                    document.Blocks.Add(new Paragraph(new Run(" "))
                    {
                        FontSize = 2,
                        Margin = new Thickness(0, 4, 0, 6),
                        BorderBrush = GreyLine,
                        BorderThickness = new Thickness(0, 0, 0, 1)
                    });
                    continue;
                }

                m = Bullet.Match(raw);
                if (m.Success)
                {
                    running = null;
                    var level = Level(m.Groups[1].Value);
                    var marker = level == 0 ? "•" : level == 1 ? "◦" : "▪";
                    document.Blocks.Add(MakeListItem(marker, m.Groups[3].Value, level, linkBrush));
                    continue;
                }

                m = Numbered.Match(raw);
                if (m.Success)
                {
                    running = null;
                    document.Blocks.Add(MakeListItem(m.Groups[2].Value + ".", m.Groups[3].Value, Level(m.Groups[1].Value), linkBrush));
                    continue;
                }

                m = Quote.Match(raw);
                if (m.Success)
                {
                    running = null;
                    var quote = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 4),
                        Padding = new Thickness(8, 0, 0, 0),
                        BorderBrush = GreyLine,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        FontStyle = FontStyles.Italic
                    };
                    AddInline(quote.Inlines, m.Groups[1].Value, false, false, linkBrush);
                    document.Blocks.Add(quote);
                    continue;
                }

                var line = raw.Trim();
                if (running == null)
                {
                    running = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    document.Blocks.Add(running);
                }
                else
                {
                    // A console wraps lines at about 80 columns; join those, keep deliberate short lines apart.
                    if (previousLine != null && previousLine.Length >= 60) running.Inlines.Add(new Run(" "));
                    else running.Inlines.Add(new LineBreak());
                }

                AddInline(running.Inlines, line, false, false, linkBrush);
                previousLine = line;
            }
        }

        static int Level(string indent)
        {
            var width = 0;
            foreach (var c in indent) width += c == '\t' ? 4 : 1;
            return System.Math.Min(4, width / 2);
        }

        static Paragraph MakeHeading(int depth, string text, Brush linkBrush)
        {
            var size = depth == 1 ? 20.0 : depth == 2 ? 17.0 : depth == 3 ? 15.0 : 13.0;
            var heading = new Paragraph
            {
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 4)
            };
            AddInline(heading.Inlines, text, false, false, linkBrush);
            return heading;
        }

        /// <summary>A list line with a hanging indent, so wrapped text lines up under the first word.</summary>
        static Paragraph MakeListItem(string marker, string text, int level, Brush linkBrush)
        {
            var item = new Paragraph
            {
                Margin = new Thickness(18 + level * 18, 0, 0, 2),
                TextIndent = -16
            };
            item.Inlines.Add(new Run(marker + "  "));
            AddInline(item.Inlines, text, false, false, linkBrush);
            return item;
        }

        static void AddInline(InlineCollection target, string text, bool bold, bool italic, Brush linkBrush)
        {
            var position = 0;
            foreach (Match m in Inline.Matches(text))
            {
                if (m.Index > position) target.Add(Styled(new Run(text.Substring(position, m.Index - position)), bold, italic));

                if (m.Groups["ctext"].Success)
                {
                    var code = Styled(new Run(m.Groups["ctext"].Value.Trim()), bold, italic);
                    code.FontFamily = new FontFamily("Consolas");
                    code.Background = Grey;
                    target.Add(code);
                }
                else if (m.Groups["b1"].Success) AddInline(target, m.Groups["b1"].Value, true, italic, linkBrush);
                else if (m.Groups["b2"].Success) AddInline(target, m.Groups["b2"].Value, true, italic, linkBrush);
                else if (m.Groups["i1"].Success) AddInline(target, m.Groups["i1"].Value, bold, true, linkBrush);
                else if (m.Groups["i2"].Success) AddInline(target, m.Groups["i2"].Value, bold, true, linkBrush);
                else
                {
                    // file:/// links locate the file in File Explorer, http(s) links open the browser
                    var url = m.Groups["lurl"].Value;
                    var link = new Hyperlink(Styled(new Run(m.Groups["ltext"].Value), bold, italic))
                    {
                        ToolTip = url.StartsWith("file:", System.StringComparison.OrdinalIgnoreCase) ? "Show in File Explorer" : url,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    if (linkBrush != null) link.Foreground = linkBrush;
                    link.Click += (s, e) => ShellLinks.Open(url);
                    target.Add(link);
                }

                position = m.Index + m.Length;
            }

            if (position < text.Length) target.Add(Styled(new Run(text.Substring(position)), bold, italic));
        }

        // Only set weight and style when needed, so a bold heading keeps its weight.
        static Run Styled(Run run, bool bold, bool italic)
        {
            if (bold) run.FontWeight = FontWeights.Bold;
            if (italic) run.FontStyle = FontStyles.Italic;
            return run;
        }

        static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
