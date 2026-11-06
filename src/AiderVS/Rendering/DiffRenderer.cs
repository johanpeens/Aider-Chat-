using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AiderVS
{
    /// <summary>
    /// Draws a unified diff the way an editor does: every line keeps the normal syntax colours of the
    /// file's language, removed and added lines get a faint red or green band, and only the leading
    /// + or - marker is coloured.
    /// </summary>
    public static class DiffRenderer
    {
        public static void Render(FlowDocument document, string diff, string fileHint,
                                  Func<TokenKind, Brush> brushFor, Brush addedBand, Brush removedBand)
        {
            document.Blocks.Clear();
            var language = fileHint ?? "";

            foreach (var line in diff.Replace("\r", "").Split('\n'))
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };

                if (line.StartsWith("+++ "))
                {
                    // "+++ b/src/New.cs": learn the file type from the header
                    var extension = ExtensionOf(line.Substring(4));
                    if (extension.Length > 0) language = extension;
                }

                if (line.StartsWith("@@") || line.StartsWith("+++") || line.StartsWith("---"))
                {
                    AddRun(paragraph, line, brushFor(TokenKind.Meta));
                }
                else if (line.StartsWith("+"))
                {
                    paragraph.Background = addedBand;
                    AddRun(paragraph, "+", brushFor(TokenKind.Added));
                    AddCode(paragraph, line.Substring(1), language, brushFor);
                }
                else if (line.StartsWith("-"))
                {
                    paragraph.Background = removedBand;
                    AddRun(paragraph, "-", brushFor(TokenKind.Removed));
                    AddCode(paragraph, line.Substring(1), language, brushFor);
                }
                else if (line.StartsWith(" "))
                {
                    AddRun(paragraph, " ", null);
                    AddCode(paragraph, line.Substring(1), language, brushFor);
                }
                else
                {
                    AddCode(paragraph, line, language, brushFor);
                }

                if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run(" "));
                document.Blocks.Add(paragraph);
            }
        }

        static void AddCode(Paragraph paragraph, string content, string language, Func<TokenKind, Brush> brushFor)
        {
            foreach (var token in SyntaxHighlighter.Highlight(content, language))
                AddRun(paragraph, token.Text, brushFor(token.Kind));
        }

        static void AddRun(Paragraph paragraph, string text, Brush brush)
        {
            if (text.Length == 0) return;
            var run = new Run(text);
            if (brush != null) run.Foreground = brush;
            paragraph.Inlines.Add(run);
        }

        static string ExtensionOf(string path)
        {
            try { return Path.GetExtension(path.Trim()).TrimStart('.'); }
            catch { return ""; }
        }
    }
}
