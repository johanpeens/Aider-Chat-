using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AiderVS
{
    public enum TokenKind
    {
        Plain, Keyword, Control, Type, Interface, Method, Local, String, Comment, DocComment,
        Number, Preprocessor, Tag, Attribute, Added, Removed, Meta
    }

    public struct Token
    {
        public Token(string text, TokenKind kind) { Text = text; Kind = kind; }
        public string Text;
        public TokenKind Kind;
    }

    /// <summary>
    /// A small, dependency-free colouriser. It follows Visual Studio's own classification:
    /// plain keywords are one colour, control-flow keywords (if, foreach, return, await...) another,
    /// then types, interfaces, methods, strings, comments, numbers and preprocessor lines.
    /// Languages: C#, Razor, JavaScript/TypeScript, Python, SQL, shell, JSON, YAML, XML/HTML/XAML and diffs.
    /// It does not parse code: type and method names are recognised by simple, well-known patterns.
    /// </summary>
    public static class SyntaxHighlighter
    {
        sealed class Language
        {
            public HashSet<string> Keywords;
            public HashSet<string> Control = new HashSet<string>();
            public HashSet<string> KnownTypes = new HashSet<string>();
            public bool CaseInsensitive;
            public string LineComment;
            public string DocComment;
            public bool BlockComment;
            public string Quotes = "\"'";
            public bool Verbatim;        // C# @"..." and $"..."
            public bool TripleQuotes;    // Python / C# raw strings
            public bool TypeGuess;       // colour PascalCase names that look like types
            public bool MethodGuess;     // colour name( as a method
            public bool Preprocessor;    // C# #region / #if lines
            public bool DollarVars;      // shell $name
            public bool LocalGuess;      // colour local variables and parameters
        }

        static readonly Language CSharp = new Language
        {
            // The control keywords are exactly the ones Roslyn classifies as "Keyword - Control".
            Control = Words("await break case catch continue default do else finally foreach for goto if in return switch throw try when while yield"),
            Keywords = Words("abstract as async base bool byte char checked class const decimal delegate double enum event explicit extern false fixed float implicit int interface internal is lock long namespace new null object operator out override params private protected public readonly ref sbyte sealed short sizeof stackalloc static string struct this true typeof uint ulong unchecked unsafe ushort using var virtual void volatile record init get set partial required"),
            KnownTypes = Words("Console String Math File Path Directory DateTime DateTimeOffset TimeSpan Guid Exception ArgumentException ArgumentNullException InvalidOperationException NotImplementedException List Dictionary HashSet Queue Stack Task ValueTask Action Func Enumerable StringBuilder Regex Environment HttpClient CancellationToken Array Convert Process Stream StreamReader StreamWriter Encoding Thread Tuple Lazy Nullable Uri Version Random Timer Stopwatch Object Boolean Byte Char Decimal Double Int32 Int64 Single UInt32 UInt64 Program Application MessageBox"),
            LineComment = "//", DocComment = "///", BlockComment = true, Verbatim = true, TripleQuotes = true,
            TypeGuess = true, MethodGuess = true, Preprocessor = true, LocalGuess = true
        };

        static readonly Language JavaScript = new Language
        {
            Control = Words("break case catch continue default do else finally for if return switch throw try while yield await of in"),
            Keywords = Words("class const debugger delete export extends false function import instanceof let new null super this true typeof var void with async static get set undefined from as interface type enum implements private public protected readonly namespace"),
            KnownTypes = Words("Promise Array Object Map Set Date Math JSON Error RegExp Number String Boolean Symbol Document Window"),
            Quotes = "\"'`", LineComment = "//", BlockComment = true, TypeGuess = true, MethodGuess = true
        };

        static readonly Language Python = new Language
        {
            Control = Words("if elif else for while break continue return try except finally raise with yield await pass assert import from"),
            Keywords = Words("and as async class def del False global in is lambda None nonlocal not or True self"),
            LineComment = "#", TripleQuotes = true, MethodGuess = true
        };

        static readonly Language Sql = new Language
        {
            Keywords = Words("select from where insert into values update set delete create table alter drop join inner left right outer on group by order having and or not null as distinct top limit union all index primary key foreign references default exists in like between case when then else end begin commit rollback declare exec procedure view"),
            CaseInsensitive = true, LineComment = "--", BlockComment = true, Quotes = "'\"", MethodGuess = true
        };

        static readonly Language Shell = new Language
        {
            Control = Words("if then else elif fi for do done while until case esac function return break continue exit foreach switch try catch finally throw"),
            Keywords = Words("in select time param begin process end export local readonly set unset"),
            LineComment = "#", DollarVars = true
        };

        static HashSet<string> Words(string list) => new HashSet<string>(list.Split(' '));

        public static string NormaliseLanguage(string lang)
        {
            switch ((lang ?? "").Trim().ToLowerInvariant())
            {
                case "cs": case "csharp": case "c#": case "java": case "c": case "cpp": case "h": case "hpp":
                    return "csharp";
                case "razor": case "cshtml": case "blazor":
                    return "razor";
                case "js": case "javascript": case "ts": case "typescript": case "jsx": case "tsx": case "mjs":
                    return "js";
                case "py": case "python": return "python";
                case "sql": return "sql";
                case "sh": case "bash": case "shell": case "zsh": case "powershell": case "ps1": case "pwsh": case "bat": case "cmd":
                    return "shell";
                case "json": case "jsonc": return "json";
                case "yaml": case "yml": return "yaml";
                case "xml": case "html": case "htm": case "xaml": case "csproj": case "config": case "svg": case "props": case "targets":
                    return "xml";
                case "diff": case "patch": return "diff";
                default: return "";
            }
        }

        public static List<Token> Highlight(string code, string language)
        {
            switch (NormaliseLanguage(language))
            {
                case "csharp": return ScanCode(code, CSharp);
                case "js": return ScanCode(code, JavaScript);
                case "python": return ScanCode(code, Python);
                case "sql": return ScanCode(code, Sql);
                case "shell": return ScanCode(code, Shell);
                case "json": return ScanJson(code);
                case "yaml": return ScanYaml(code);
                case "xml": return ScanXml(code, false);
                case "razor": return ScanXml(code, true);
                case "diff": return ScanDiff(code);
                default: return new List<Token> { new Token(code, TokenKind.Plain) };
            }
        }

        // ---- C-like / Python / SQL / shell ---------------------------------------

        static readonly Regex InterfaceName = new Regex(@"^I[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

        static List<Token> ScanCode(string code, Language lang)
        {
            var tokens = new List<Token>();
            var plain = new StringBuilder();
            var n = code.Length;
            var i = 0;
            var locals = lang.LocalGuess ? FindLocals(code) : null;

            void Flush()
            {
                if (plain.Length == 0) return;
                tokens.Add(new Token(plain.ToString(), TokenKind.Plain));
                plain.Clear();
            }

            void Add(TokenKind kind, string text)
            {
                Flush();
                tokens.Add(new Token(text, kind));
            }

            // $"text {expression} text": colour the text as a string and the {expression} as code.
            void AddInterpolated(string literal)
            {
                var part = new StringBuilder();
                var k = 0;
                while (k < literal.Length)
                {
                    if (literal[k] == '{')
                    {
                        if (k + 1 < literal.Length && literal[k + 1] == '{') { part.Append("{{"); k += 2; continue; }

                        var close = FindClosing(literal, k, '{', '}');
                        if (close < 0) { part.Append(literal, k, literal.Length - k); break; }

                        if (part.Length > 0) { Add(TokenKind.String, part.ToString()); part.Clear(); }
                        Add(TokenKind.Plain, "{");
                        tokens.AddRange(ScanCode(literal.Substring(k + 1, close - k - 1), lang));
                        Add(TokenKind.Plain, "}");
                        k = close + 1;
                        continue;
                    }
                    part.Append(literal[k]);
                    k++;
                }
                if (part.Length > 0) Add(TokenKind.String, part.ToString());
            }

            while (i < n)
            {
                var c = code[i];

                // C# preprocessor line: #region, #if, #pragma ...
                if (lang.Preprocessor && c == '#' && i + 1 < n && char.IsLetter(code[i + 1]) && OnlySpaceBeforeOnLine(code, i))
                {
                    var end = code.IndexOf('\n', i);
                    if (end < 0) end = n;
                    Add(TokenKind.Preprocessor, code.Substring(i, end - i));
                    i = end;
                    continue;
                }

                // Line comment (and /// documentation comments)
                if (lang.LineComment != null && string.CompareOrdinal(code, i, lang.LineComment, 0, lang.LineComment.Length) == 0)
                {
                    var end = code.IndexOf('\n', i);
                    if (end < 0) end = n;
                    var isDoc = lang.DocComment != null && string.CompareOrdinal(code, i, lang.DocComment, 0, lang.DocComment.Length) == 0;
                    Add(isDoc ? TokenKind.DocComment : TokenKind.Comment, code.Substring(i, end - i));
                    i = end;
                    continue;
                }

                // Block comment
                if (lang.BlockComment && c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                    Add(TokenKind.Comment, code.Substring(i, end - i));
                    i = end;
                    continue;
                }

                // Shell variables: $name, ${name}
                if (lang.DollarVars && c == '$' && i + 1 < n && (char.IsLetter(code[i + 1]) || code[i + 1] == '_' || code[i + 1] == '{'))
                {
                    var j = i + 1;
                    if (code[j] == '{')
                    {
                        var close = code.IndexOf('}', j);
                        j = close < 0 ? n : close + 1;
                    }
                    else
                    {
                        while (j < n && IsIdentChar(code[j])) j++;
                    }
                    Add(TokenKind.Attribute, code.Substring(i, j - i));
                    i = j;
                    continue;
                }

                // Strings (with C# @ and $ prefixes)
                var prefix = 0;
                var verbatim = false;
                var interpolated = false;
                if (lang.Verbatim)
                {
                    while (i + prefix < n && prefix < 2 && (code[i + prefix] == '@' || code[i + prefix] == '$'))
                    {
                        if (code[i + prefix] == '@') verbatim = true;
                        else interpolated = true;
                        prefix++;
                    }
                    if (prefix > 0 && (i + prefix >= n || code[i + prefix] != '"'))
                    {
                        prefix = 0;
                        verbatim = false;
                        interpolated = false;
                    }
                }

                var quoteAt = i + prefix;
                if (quoteAt < n && lang.Quotes.IndexOf(code[quoteAt]) >= 0)
                {
                    var quote = code[quoteAt];

                    if (lang.TripleQuotes && quoteAt + 2 < n && code[quoteAt + 1] == quote && code[quoteAt + 2] == quote)
                    {
                        var closing = new string(quote, 3);
                        var end = code.IndexOf(closing, quoteAt + 3, StringComparison.Ordinal);
                        end = end < 0 ? n : end + 3;
                        Add(TokenKind.String, code.Substring(i, end - i));
                        i = end;
                        continue;
                    }

                    var j = quoteAt + 1;
                    while (j < n)
                    {
                        if (verbatim && code[j] == '"' && j + 1 < n && code[j + 1] == '"') { j += 2; continue; }
                        if (!verbatim && code[j] == '\\' && j + 1 < n) { j += 2; continue; }
                        if (code[j] == quote) { j++; break; }
                        if (code[j] == '\n' && !verbatim && quote != '`') break;
                        j++;
                    }
                    var literal = code.Substring(i, Math.Min(j, n) - i);
                    if (interpolated) AddInterpolated(literal);
                    else Add(TokenKind.String, literal);
                    i = Math.Min(j, n);
                    continue;
                }

                // Numbers
                if (char.IsDigit(c) && (i == 0 || !IsIdentChar(code[i - 1])))
                {
                    var j = i;
                    while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '_' ||
                                     (code[j] == '.' && j + 1 < n && char.IsDigit(code[j + 1]))))
                        j++;
                    Add(TokenKind.Number, code.Substring(i, j - i));
                    i = j;
                    continue;
                }

                // Words
                if (char.IsLetter(c) || c == '_')
                {
                    var j = i;
                    while (j < n && IsIdentChar(code[j])) j++;
                    var word = code.Substring(i, j - i);
                    var key = lang.CaseInsensitive ? word.ToLowerInvariant() : word;

                    if (lang.Control.Contains(key)) Add(TokenKind.Control, word);
                    else if (lang.Keywords.Contains(key)) Add(TokenKind.Keyword, word);
                    else
                    {
                        var kind = ClassifyName(code, i, j, word, lang, locals);
                        if (kind == TokenKind.Plain) plain.Append(word);
                        else Add(kind, word);
                    }

                    i = j;
                    continue;
                }

                plain.Append(c);
                i++;
            }

            Flush();
            return tokens;
        }

        static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        static bool OnlySpaceBeforeOnLine(string code, int index)
        {
            for (var i = index - 1; i >= 0 && code[i] != '\n'; i--)
                if (code[i] != ' ' && code[i] != '\t') return false;
            return true;
        }

        /// <summary>Decides whether an identifier is a type, interface, method or just a name.</summary>
        static TokenKind ClassifyName(string code, int start, int end, string word, Language lang, HashSet<string> locals)
        {
            var next = NextNonSpace(code, end);
            var prevWord = PreviousWord(code, start);

            // Names declared as local variables or parameters in this snippet (not members: x.Name).
            if (locals != null && locals.Contains(word) && next != '(' && PreviousNonSpace(code, start) != '.')
                return TokenKind.Local;

            if (lang.TypeGuess)
            {
                if (lang.KnownTypes.Contains(word) && next != '(') return TypeOrInterface(word);

                // names that follow new / class / interface / is / as ... are types
                switch (prevWord)
                {
                    case "new": case "class": case "struct": case "interface": case "enum": case "record":
                    case "typeof": case "is": case "as":
                        return prevWord == "interface" ? TokenKind.Interface : TypeOrInterface(word);
                }
            }

            if (lang.MethodGuess && next == '(') return TokenKind.Method;

            if (lang.TypeGuess && word.Length >= 2 && char.IsUpper(word[0]) && PreviousNonSpace(code, start) != '.')
            {
                // PascalCase followed by . < > or another identifier looks like a type: Foo.Bar, List<int>, Foo x
                if (next == '.' || next == '<' || next == '>' || char.IsLetter(next) || next == '_')
                    return TypeOrInterface(word);
            }

            return TokenKind.Plain;
        }

        static readonly HashSet<string> DeclarationModifiers = new HashSet<string>
        {
            "public", "private", "protected", "internal", "static", "readonly", "const", "virtual", "override",
            "abstract", "sealed", "extern", "new", "partial", "required", "volatile"
        };

        // "Type name" followed by = ; , ) or "in": a local variable or a parameter.
        static readonly Regex LocalDeclaration = new Regex(
            @"(?<![\w.])(?:var|int|uint|long|ulong|short|byte|char|bool|string|double|float|decimal|object|[A-Z][A-Za-z0-9_]*(?:<[^<>\n]*>)?(?:\[\])?\??)\s+([A-Za-z_]\w*)\s*(?==|;|,|\)|\bin\b)",
            RegexOptions.Compiled);

        static HashSet<string> FindLocals(string code)
        {
            var names = new HashSet<string>();
            foreach (Match match in LocalDeclaration.Matches(code))
            {
                // public int Count = 0;  is a field, not a local.
                if (DeclarationModifiers.Contains(PreviousWord(code, match.Index))) continue;

                var name = match.Groups[1].Value;
                if (CSharp.Keywords.Contains(name) || CSharp.Control.Contains(name)) continue;
                names.Add(name);
            }
            return names;
        }

        /// <summary>Index of the bracket that closes the one at <paramref name="open"/>, or -1 if it is still open.</summary>
        static int FindClosing(string code, int open, char openChar, char closeChar)
        {
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                var c = code[i];

                if (c == '"' || c == '\'')
                {
                    var j = i + 1;
                    while (j < code.Length && code[j] != c && code[j] != '\n') j += code[j] == '\\' ? 2 : 1;
                    i = j;
                    continue;
                }

                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    var end = code.IndexOf('\n', i);
                    if (end < 0) return -1;
                    i = end;
                    continue;
                }

                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return -1;
                    i = end + 1;
                    continue;
                }

                if (c == openChar) depth++;
                else if (c == closeChar)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static TokenKind TypeOrInterface(string word) =>
            InterfaceName.IsMatch(word) ? TokenKind.Interface : TokenKind.Type;

        static char NextNonSpace(string code, int index)
        {
            for (var i = index; i < code.Length; i++)
                if (code[i] != ' ' && code[i] != '\t') return code[i];
            return '\0';
        }

        static char PreviousNonSpace(string code, int index)
        {
            for (var i = index - 1; i >= 0; i--)
                if (code[i] != ' ' && code[i] != '\t') return code[i];
            return '\0';
        }

        static string PreviousWord(string code, int index)
        {
            var i = index - 1;
            while (i >= 0 && (code[i] == ' ' || code[i] == '\t')) i--;
            var end = i + 1;
            while (i >= 0 && IsIdentChar(code[i])) i--;
            return end > i + 1 ? code.Substring(i + 1, end - i - 1) : "";
        }

        // ---- JSON -----------------------------------------------------------------

        static List<Token> ScanJson(string code)
        {
            var tokens = new List<Token>();
            var plain = new StringBuilder();
            var n = code.Length;
            var i = 0;

            void Flush()
            {
                if (plain.Length == 0) return;
                tokens.Add(new Token(plain.ToString(), TokenKind.Plain));
                plain.Clear();
            }

            while (i < n)
            {
                var c = code[i];

                if (c == '"')
                {
                    var j = i + 1;
                    while (j < n && code[j] != '"' && code[j] != '\n') j += code[j] == '\\' ? 2 : 1;
                    j = Math.Min(j + 1, n);

                    var k = j;
                    while (k < n && (code[k] == ' ' || code[k] == '\t')) k++;
                    Flush();
                    tokens.Add(new Token(code.Substring(i, j - i), k < n && code[k] == ':' ? TokenKind.Attribute : TokenKind.String));
                    i = j;
                    continue;
                }

                if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(code[i + 1])))
                {
                    var j = i + 1;
                    while (j < n && (char.IsDigit(code[j]) || code[j] == '.' || code[j] == 'e' || code[j] == 'E' || code[j] == '+' || code[j] == '-')) j++;
                    Flush();
                    tokens.Add(new Token(code.Substring(i, j - i), TokenKind.Number));
                    i = j;
                    continue;
                }

                if (char.IsLetter(c))
                {
                    var j = i;
                    while (j < n && char.IsLetter(code[j])) j++;
                    var word = code.Substring(i, j - i);
                    if (word == "true" || word == "false" || word == "null")
                    {
                        Flush();
                        tokens.Add(new Token(word, TokenKind.Keyword));
                    }
                    else plain.Append(word);
                    i = j;
                    continue;
                }

                plain.Append(c);
                i++;
            }

            Flush();
            return tokens;
        }

        // ---- YAML -----------------------------------------------------------------

        static readonly Regex YamlKey = new Regex(@"^(\s*(?:-\s+)?)([^\s:#][^:#]*?)(:)(\s|$)", RegexOptions.Compiled);

        static List<Token> ScanYaml(string code)
        {
            var tokens = new List<Token>();
            var start = 0;

            while (start < code.Length)
            {
                var end = code.IndexOf('\n', start);
                var lineEnd = end < 0 ? code.Length : end + 1;
                var line = code.Substring(start, lineEnd - start);
                start = lineEnd;

                if (line.TrimStart().StartsWith("#"))
                {
                    tokens.Add(new Token(line, TokenKind.Comment));
                    continue;
                }

                var rest = line;
                var match = YamlKey.Match(line);
                if (match.Success)
                {
                    if (match.Groups[1].Length > 0) tokens.Add(new Token(match.Groups[1].Value, TokenKind.Plain));
                    tokens.Add(new Token(match.Groups[2].Value, TokenKind.Attribute));
                    tokens.Add(new Token(":", TokenKind.Plain));
                    rest = line.Substring(match.Groups[3].Index + 1);
                }

                var comment = rest.IndexOf(" #", StringComparison.Ordinal);
                var body = comment >= 0 ? rest.Substring(0, comment) : rest;
                var trimmed = body.Trim();

                TokenKind kind = TokenKind.Plain;
                if (trimmed.StartsWith("\"") || trimmed.StartsWith("'")) kind = TokenKind.String;
                else if (trimmed == "true" || trimmed == "false" || trimmed == "null" || trimmed == "~") kind = TokenKind.Keyword;
                else if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) kind = TokenKind.Number;

                if (body.Length > 0) tokens.Add(new Token(body, kind));
                if (comment >= 0) tokens.Add(new Token(rest.Substring(comment), TokenKind.Comment));
            }
            return tokens;
        }

        // ---- XML / HTML / XAML / Razor --------------------------------------------

        static readonly HashSet<string> RazorDirectives = Words("page using inject inherits namespace attribute layout implements typeparam rendermode model preservewhitespace");
        static readonly HashSet<string> RazorControl = Words("if for foreach while switch do lock try");

        static List<Token> ScanXml(string code, bool razor)
        {
            var tokens = new List<Token>();
            var plain = new StringBuilder();
            var n = code.Length;
            var i = 0;

            void Flush()
            {
                if (plain.Length == 0) return;
                tokens.Add(new Token(plain.ToString(), TokenKind.Plain));
                plain.Clear();
            }

            void Add(TokenKind kind, string text)
            {
                Flush();
                tokens.Add(new Token(text, kind));
            }

            // Scans C# between brackets: the open bracket is plain, the body is coloured as C#.
            void EmitCSharpBody(int bodyStart, int bodyEnd)
            {
                Flush();
                if (bodyEnd > bodyStart) tokens.AddRange(ScanCode(code.Substring(bodyStart, bodyEnd - bodyStart), CSharp));
            }

            // @code { ... }, @{ ... }, @if (...), @page "/x", @expression: returns false when "@" is just text.
            bool TryRazor()
            {
                if (i + 1 >= n) return false;
                var next = code[i + 1];

                if (next == '@') { plain.Append("@@"); i += 2; return true; }

                if (next == '{')
                {
                    Add(TokenKind.Keyword, "@");
                    var close = FindClosing(code, i + 1, '{', '}');
                    Add(TokenKind.Plain, "{");
                    EmitCSharpBody(i + 2, close < 0 ? n : close);
                    if (close >= 0) Add(TokenKind.Plain, "}");
                    i = close < 0 ? n : close + 1;
                    return true;
                }

                if (next == '(')
                {
                    Add(TokenKind.Keyword, "@");
                    var close = FindClosing(code, i + 1, '(', ')');
                    var end = close < 0 ? n : close + 1;
                    EmitCSharpBody(i + 1, end);
                    i = end;
                    return true;
                }

                if (!char.IsLetter(next)) return false;

                var k = i + 1;
                while (k < n && IsIdentChar(code[k])) k++;
                var word = code.Substring(i + 1, k - i - 1);

                if (word == "code" || word == "functions")
                {
                    Add(TokenKind.Keyword, "@" + word);
                    var s = k;
                    while (s < n && char.IsWhiteSpace(code[s])) s++;
                    if (s < n && code[s] == '{')
                    {
                        if (s > k) Add(TokenKind.Plain, code.Substring(k, s - k));
                        var close = FindClosing(code, s, '{', '}');
                        Add(TokenKind.Plain, "{");
                        EmitCSharpBody(s + 1, close < 0 ? n : close);
                        if (close >= 0) Add(TokenKind.Plain, "}");
                        i = close < 0 ? n : close + 1;
                    }
                    else i = k;
                    return true;
                }

                if (RazorDirectives.Contains(word))
                {
                    Add(TokenKind.Keyword, "@" + word);
                    var end = code.IndexOf('\n', k);
                    if (end < 0) end = n;
                    EmitCSharpBody(k, end);
                    i = end;
                    return true;
                }

                if (RazorControl.Contains(word))
                {
                    Add(TokenKind.Control, "@" + word);
                    var s = k;
                    while (s < n && code[s] == ' ') s++;
                    if (s < n && code[s] == '(')
                    {
                        var close = FindClosing(code, s, '(', ')');
                        var end = close < 0 ? n : close + 1;
                        if (s > k) Add(TokenKind.Plain, code.Substring(k, s - k));
                        EmitCSharpBody(s, end);
                        i = end;
                    }
                    else i = k;
                    return true;
                }

                // @count, @Model.Name, @Format(x)
                var e2 = k;
                while (e2 < n && (IsIdentChar(code[e2]) || code[e2] == '.')) e2++;
                while (e2 > k && code[e2 - 1] == '.') e2--;
                if (e2 < n && code[e2] == '(')
                {
                    var close = FindClosing(code, e2, '(', ')');
                    e2 = close < 0 ? n : close + 1;
                }
                Add(TokenKind.Keyword, "@");
                EmitCSharpBody(i + 1, e2);
                i = e2;
                return true;
            }

            while (i < n)
            {
                if (string.CompareOrdinal(code, i, "<!--", 0, 4) == 0)
                {
                    var end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    Add(TokenKind.Comment, code.Substring(i, end - i));
                    i = end;
                    continue;
                }

                // Razor directives and code blocks: @page, @code, @inject, @foreach ...
                if (razor && code[i] == '@' && TryRazor()) continue;

                if (code[i] != '<')
                {
                    plain.Append(code[i]);
                    i++;
                    continue;
                }

                // Opening bracket, possibly </ or <?
                var j = i + 1;
                if (j < n && (code[j] == '/' || code[j] == '?' || code[j] == '!')) j++;
                Add(TokenKind.Tag, code.Substring(i, j - i));

                // Tag name
                var nameStart = j;
                while (j < n && !char.IsWhiteSpace(code[j]) && code[j] != '>' && code[j] != '/') j++;
                if (j > nameStart) Add(TokenKind.Tag, code.Substring(nameStart, j - nameStart));

                // Attributes up to the closing bracket
                while (j < n && code[j] != '>')
                {
                    var c = code[j];

                    if (c == '"' || c == '\'')
                    {
                        var k = j + 1;
                        while (k < n && code[k] != c) k++;
                        k = Math.Min(k + 1, n);
                        Add(TokenKind.String, code.Substring(j, k - j));
                        j = k;
                    }
                    else if (char.IsLetter(c) || c == '_' || c == ':' || c == '@')
                    {
                        var k = j;
                        while (k < n && !char.IsWhiteSpace(code[k]) && code[k] != '=' && code[k] != '>' && code[k] != '/') k++;
                        Add(TokenKind.Attribute, code.Substring(j, k - j));
                        j = k;
                    }
                    else if (c == '/' || c == '?')
                    {
                        Add(TokenKind.Tag, c.ToString());
                        j++;
                    }
                    else
                    {
                        plain.Append(c);
                        j++;
                    }
                }

                if (j < n) { Add(TokenKind.Tag, ">"); j++; }
                i = j;
            }

            Flush();
            return tokens;
        }

        // ---- Diff -----------------------------------------------------------------

        static List<Token> ScanDiff(string code)
        {
            var tokens = new List<Token>();
            var start = 0;
            while (start < code.Length)
            {
                var end = code.IndexOf('\n', start);
                var lineEnd = end < 0 ? code.Length : end + 1;
                var line = code.Substring(start, lineEnd - start);

                TokenKind kind;
                if (line.StartsWith("@@") || line.StartsWith("+++") || line.StartsWith("---")) kind = TokenKind.Meta;
                else if (line.StartsWith("+")) kind = TokenKind.Added;
                else if (line.StartsWith("-")) kind = TokenKind.Removed;
                else kind = TokenKind.Plain;

                tokens.Add(new Token(line, kind));
                start = lineEnd;
            }
            return tokens;
        }
    }
}
