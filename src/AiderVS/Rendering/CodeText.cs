using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;

namespace AiderVS
{
    /// <summary>
    /// Attached property that fills a Panel from chat text. Normal text stays plain; fenced ``` code
    /// blocks become coloured, monospaced blocks with a small copy icon in the top-right corner.
    /// While Aider streams its answer only the block that changed is redrawn.
    /// </summary>
    public static class CodeText
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(CodeText), new PropertyMetadata(null, OnSourceChanged));

        public static string GetSource(DependencyObject d) => (string)d.GetValue(SourceProperty);
        public static void SetSource(DependencyObject d, string value) => d.SetValue(SourceProperty, value);

        static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Panel panel) Render(panel, e.NewValue as string ?? "");
        }

        // ---- Splitting the text into plain and code blocks ------------------------

        sealed class Block
        {
            public bool IsCode;
            public string Language = "";
            public string FileHint = "";   // file type taken from the line before a diff block
            public string Text = "";
        }

        public static IReadOnlyList<(bool IsCode, string Language, string Text)> Split(string text)
        {
            var result = new List<(bool, string, string)>();
            foreach (var b in ParseBlocks(text)) result.Add((b.IsCode, b.Language, b.Text));
            return result;
        }

        static List<Block> ParseBlocks(string text)
        {
            var blocks = new List<Block>();
            var current = new System.Text.StringBuilder();
            Block open = null;

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = rawLine.TrimStart();

                if (trimmed.StartsWith("```"))
                {
                    if (open == null)
                    {
                        if (current.Length > 0) blocks.Add(new Block { Text = current.ToString() });
                        current.Clear();

                        var language = trimmed.Substring(3).Trim();
                        var hint = GuessLanguageFromPreviousLine(blocks);
                        open = new Block
                        {
                            IsCode = true,
                            Language = language.Length == 0 ? hint : language,
                            FileHint = language.Length == 0 ? "" : hint
                        };
                    }
                    else
                    {
                        open.Text = current.ToString().TrimEnd('\n');
                        blocks.Add(open);
                        current.Clear();
                        open = null;
                    }
                    continue;
                }

                current.Append(rawLine).Append('\n');
            }

            // An unclosed fence is normal while Aider is still streaming.
            if (open != null)
            {
                open.Text = current.ToString().TrimEnd('\n');
                blocks.Add(open);
            }
            else if (current.Length > 0)
            {
                blocks.Add(new Block { Text = current.ToString() });
            }

            return blocks;
        }

        /// <summary>Aider prints the file name on the line before an unlabelled fence.</summary>
        static string GuessLanguageFromPreviousLine(List<Block> blocks)
        {
            if (blocks.Count == 0 || blocks[blocks.Count - 1].IsCode) return "";

            // "Changed: `Program.cs`" or just "Program.cs": the last word is the file name
            var lines = blocks[blocks.Count - 1].Text.TrimEnd('\n').Split('\n');
            var words = lines[lines.Length - 1].Trim().Split(' ');
            var last = words[words.Length - 1].Trim('`', '*', ':', '(', ')', ',');
            if (last.Length == 0) return "";

            try { return Path.GetExtension(last).TrimStart('.'); }
            catch { return ""; }
        }

        // ---- Building the view ----------------------------------------------------

        enum Kind { Plain, Code, Note }

        sealed class ViewBlock
        {
            public Kind Kind;
            public string Language = "";
            public string FileHint = "";
            public string Text = "";
        }

        /// <summary>Remembered on each child element so unchanged blocks are not redrawn.</summary>
        sealed class ViewTag
        {
            public Kind Kind;
            public RichTextBox Box;
            public TextBlock Note;
            public string LastText;
            public string LastLanguage;
            public string Code = "";
        }

        static void Render(Panel panel, string text)
        {
            var palette = Palette.For(IsDark());
            var views = new List<ViewBlock>();

            foreach (var block in ParseBlocks(text))
            {
                if (!block.IsCode)
                {
                    var plain = block.Text.Trim('\n');
                    if (plain.Length > 0) views.Add(new ViewBlock { Kind = Kind.Plain, Text = plain });
                }
                else if (SyntaxHighlighter.NormaliseLanguage(block.Language) == "diff" && block.Text.Trim().Length == 0)
                {
                    // Aider prints a diff of your file, so an unchanged file gives an empty diff block.
                    views.Add(new ViewBlock { Kind = Kind.Note, Text = "(no changes to the file)" });
                }
                else
                {
                    views.Add(new ViewBlock { Kind = Kind.Code, Language = block.Language, FileHint = block.FileHint, Text = block.Text });
                }
            }

            for (var i = 0; i < views.Count; i++)
            {
                var view = views[i];
                var element = i < panel.Children.Count ? panel.Children[i] as FrameworkElement : null;
                var tag = element?.Tag as ViewTag;

                if (tag == null || tag.Kind != view.Kind)
                {
                    element = Create(view.Kind, out tag);
                    if (i < panel.Children.Count) panel.Children[i] = element;
                    else panel.Children.Add(element);
                }

                Update(tag, view, palette);
            }

            while (panel.Children.Count > views.Count)
                panel.Children.RemoveAt(panel.Children.Count - 1);
        }

        static FrameworkElement Create(Kind kind, out ViewTag tag)
        {
            tag = new ViewTag { Kind = kind };

            switch (kind)
            {
                case Kind.Code:
                {
                    var box = NewBox();
                    box.FontFamily = new FontFamily("Consolas");
                    box.Margin = new Thickness(8, 6, 30, 6);
                    tag.Box = box;

                    var grid = new Grid();
                    grid.Children.Add(box);
                    grid.Children.Add(NewCopyButton(tag));

                    return new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 2, 0, 6),
                        Background = Palette.CodeBackground,
                        Child = grid,
                        Tag = tag
                    };
                }

                case Kind.Note:
                {
                    var note = new TextBlock
                    {
                        FontStyle = FontStyles.Italic,
                        Opacity = 0.7,
                        Margin = new Thickness(0, 2, 0, 6),
                        TextWrapping = TextWrapping.Wrap,
                        Tag = tag
                    };
                    note.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                    tag.Note = note;
                    return note;
                }

                default:
                {
                    var box = NewBox();
                    box.Margin = new Thickness(0, 0, 0, 4);
                    box.Tag = tag;
                    tag.Box = box;
                    return box;
                }
            }
        }

        static void Update(ViewTag tag, ViewBlock view, Palette palette)
        {
            var languageKey = view.Language + "|" + view.FileHint;
            if (tag.LastText == view.Text && tag.LastLanguage == languageKey) return;
            tag.LastText = view.Text;
            tag.LastLanguage = languageKey;

            if (tag.Kind == Kind.Note)
            {
                tag.Note.Text = view.Text;
                return;
            }

            var document = tag.Box.Document;

            if (tag.Kind != Kind.Code)
            {
                // Everything between code blocks is rendered as Markdown.
                MarkdownRenderer.Render(document, view.Text, palette.BrushFor(TokenKind.Attribute));
                return;
            }

            tag.Code = view.Text;

            if (SyntaxHighlighter.NormaliseLanguage(view.Language) == "diff")
            {
                DiffRenderer.Render(document, view.Text, view.FileHint, palette.BrushFor, Palette.AddedBand, Palette.RemovedBand);
                return;
            }

            document.Blocks.Clear();
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            foreach (var token in SyntaxHighlighter.Highlight(view.Text, view.Language))
                AddText(paragraph, token.Text, palette.BrushFor(token.Kind));
            document.Blocks.Add(paragraph);
        }

        static void AddText(Paragraph paragraph, string text, Brush brush)
        {
            var lines = text.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                if (lines[i].Length == 0) continue;

                var run = new Run(lines[i]);
                if (brush != null) run.Foreground = brush;
                paragraph.Inlines.Add(run);
            }
        }

        // ---- Controls -------------------------------------------------------------

        static RichTextBox NewBox()
        {
            var box = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,   // lets the links in a reply be clicked
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                IsInactiveSelectionHighlightEnabled = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            box.Document.PagePadding = new Thickness(0);
            box.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            box.PreviewMouseWheel += ForwardWheel;
            return box;
        }

        /// <summary>A RichTextBox swallows the mouse wheel; hand it on so the chat still scrolls.</summary>
        static void ForwardWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;

            var parent = VisualTreeHelper.GetParent((DependencyObject)sender) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }

        static ControlTemplate _copyTemplate;

        static ControlTemplate CopyTemplate => _copyTemplate ?? (_copyTemplate = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border Name='bd' Background='Transparent' CornerRadius='3'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            "<Setter TargetName='bd' Property='Background' Value='#33808080'/></Trigger></ControlTemplate.Triggers>" +
            "</ControlTemplate>"));

        static Button NewCopyButton(ViewTag tag)
        {
            const string CopyGlyph = "";   // Segoe MDL2 Assets: Copy
            const string DoneGlyph = "";   // Segoe MDL2 Assets: CheckMark

            var glyph = new TextBlock { Text = CopyGlyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            var button = new Button
            {
                Content = glyph,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 4, 0),
                Focusable = false,
                Cursor = Cursors.Hand,
                Opacity = 0.6,
                ToolTip = "Copy code",
                Template = CopyTemplate
            };

            button.MouseEnter += (s, e) => button.Opacity = 1;
            button.MouseLeave += (s, e) => button.Opacity = 0.6;

            DispatcherTimer reset = null;
            button.Click += (s, e) =>
            {
                try { Clipboard.SetText(tag.Code ?? ""); }
                catch { return; } // another program is holding the clipboard

                glyph.Text = DoneGlyph;
                button.ToolTip = "Copied";

                if (reset == null)
                {
                    reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
                    reset.Tick += (a, b) =>
                    {
                        reset.Stop();
                        glyph.Text = CopyGlyph;
                        button.ToolTip = "Copy code";
                    };
                }
                reset.Stop();
                reset.Start();
            };

            return button;
        }

        static bool IsDark()
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
            }
            catch { return true; }
        }

        // ---- Colours --------------------------------------------------------------

        sealed class Palette
        {
            // Dark values follow Visual Studio's own dark theme; light values its default light theme.
            static readonly Palette Dark = new Palette(
                keyword: "569CD6", control: "D8A0DF", type: "4EC9B0", iface: "B8D7A3", method: "DCDCAA", local: "9CDCFE",
                str: "D69D85", comment: "57A64A", doc: "608B4E", number: "B5CEA8", preproc: "9B9B9B",
                tag: "569CD6", attribute: "9CDCFE", added: "89D185", removed: "F48771", meta: "9CDCFE");

            static readonly Palette Light = new Palette(
                keyword: "0000FF", control: "8F08C4", type: "2B91AF", iface: "2B91AF", method: "74531F", local: "1F377F",
                str: "A31515", comment: "008000", doc: "008000", number: "098658", preproc: "808080",
                tag: "800000", attribute: "FF0000", added: "098658", removed: "C72E0F", meta: "0451A5");

            public static Palette For(bool dark) => dark ? Dark : Light;

            public static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));

            // Faint bands behind added and removed diff lines, so the code colours stay readable.
            public static readonly Brush AddedBand = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x3F, 0xB9, 0x50)));
            public static readonly Brush RemovedBand = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xF8, 0x51, 0x49)));

            readonly Dictionary<TokenKind, Brush> _brushes;

            Palette(string keyword, string control, string type, string iface, string method, string local, string str,
                    string comment, string doc, string number, string preproc,
                    string tag, string attribute, string added, string removed, string meta)
            {
                _brushes = new Dictionary<TokenKind, Brush>
                {
                    [TokenKind.Keyword] = Make(keyword),
                    [TokenKind.Control] = Make(control),
                    [TokenKind.Type] = Make(type),
                    [TokenKind.Interface] = Make(iface),
                    [TokenKind.Method] = Make(method),
                    [TokenKind.Local] = Make(local),
                    [TokenKind.String] = Make(str),
                    [TokenKind.Comment] = Make(comment),
                    [TokenKind.DocComment] = Make(doc),
                    [TokenKind.Number] = Make(number),
                    [TokenKind.Preprocessor] = Make(preproc),
                    [TokenKind.Tag] = Make(tag),
                    [TokenKind.Attribute] = Make(attribute),
                    [TokenKind.Added] = Make(added),
                    [TokenKind.Removed] = Make(removed),
                    [TokenKind.Meta] = Make(meta)
                };
            }

            public Brush BrushFor(TokenKind kind) => _brushes.TryGetValue(kind, out var brush) ? brush : null;

            static Brush Make(string hex)
            {
                var color = (Color)ColorConverter.ConvertFromString("#" + hex);
                return Freeze(new SolidColorBrush(color));
            }

            static Brush Freeze(SolidColorBrush brush)
            {
                brush.Freeze();
                return brush;
            }
        }
    }
}
