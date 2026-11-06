using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AiderVS
{
    /// <summary>One skill after the adapter has translated it for Aider.</summary>
    public sealed class AdaptedSkill
    {
        public Skill Skill { get; set; }
        public string Why { get; set; }                 // why it was chosen
        public string Text { get; set; }                // what Aider is given
        public List<string> Attached { get; } = new List<string>();     // reference files included in Text
        public List<string> Unsupported { get; } = new List<string>();  // parts of the skill Aider cannot do
    }

    public sealed class SkillPlan
    {
        public List<AdaptedSkill> Skills { get; } = new List<AdaptedSkill>();

        /// <summary>The user's message with skill names (/name, $name) removed.</summary>
        public string Message { get; set; }

        /// <summary>Read-only reference file handed to Aider with --read, or null.</summary>
        public string ContextFile { get; set; }

        public int Tokens { get; set; }

        /// <summary>Markdown for the chat: what was chosen and what could not be carried over.</summary>
        public string Report { get; set; } = "";
    }

    /// <summary>
    /// THE MIDDLE LAYER between skill files and Aider.
    ///
    /// Skills are written for agents that can run tools: the model reads a catalogue of names and
    /// descriptions, decides which skill applies, then reads SKILL.md and any files it mentions.
    /// Aider cannot do any of that: it has no tools, and a small local model is a poor judge of
    /// which skill fits. So this layer does the agent's part and hands Aider plain reference text:
    ///
    ///   1. Discover   find SKILL.md folders (SkillStore) and read them leniently (SkillParser)
    ///   2. Select     you name a skill, it is always-on, it is still active in this chat, or the
    ///                 adapter matches the request to the descriptions (words first, then the
    ///                 local model confirms from a short list)
    ///   3. Adapt      strip the frontmatter, fill in $ARGUMENTS and ${CLAUDE_SKILL_DIR}, replace
    ///                 !`command` with a note, attach the reference files the skill points to, and
    ///                 list what Aider cannot do (scripts, tools, forked agents)
    ///   4. Deliver    write one read-only file for aider --read, so the text is not repeated in
    ///                 the chat history, plus a short reminder in the message
    /// </summary>
    public static class SkillAdapter
    {
        const int MaxSkills = 4;
        const int MaxAttachedFiles = 3;
        const int MaxAttachedChars = 3000;

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Only sent when a skill needs tools Aider does not have; otherwise it would be wasted prompt space.
        static readonly string Environment_Note =
            "You cannot run commands or tools. Follow the skill by editing the files in the chat; where it says to run something, give the user the command instead.";

        // ---- Planning ----------------------------------------------------------

        public static async Task<SkillPlan> PlanAsync(
            SkillStore store, AiderSettings settings, string userText, IEnumerable<string> contextFiles,
            ISet<string> sessionActive, string stateFolder, string workFolder, CancellationToken ct)
        {
            var plan = new SkillPlan { Message = userText };
            if (store == null || store.Skills.Count == 0) return plan;

            var disabled = new HashSet<string>(settings.DisabledSkills, StringComparer.OrdinalIgnoreCase);
            var available = store.Skills.Where(s => !disabled.Contains(s.Name)).ToList();
            var files = contextFiles?.ToList() ?? new List<string>();

            var chosen = new List<(Skill Skill, string Why)>();
            void Pick(Skill skill, string why)
            {
                if (skill == null || chosen.Count >= MaxSkills) return;
                if (chosen.Any(c => c.Skill == skill)) return;
                chosen.Add((skill, why));
            }

            // 1. The user names a skill: /name at the start or $name anywhere.
            var message = ExtractNamedSkills(available, userText, out var named);
            foreach (var skill in named) Pick(skill, "you named it");
            plan.Message = message;

            // 2. Always-on skills, and skills still active from earlier in this chat.
            // Skills do not carry over between messages: every one that is sent costs prompt space each time.
            foreach (var name in settings.AlwaysSkills) Pick(available.FirstOrDefault(s => Same(s.Name, name)), "always on");

            // 3. Match the request to skill descriptions.
            if (settings.SkillsEnabled && chosen.Count < MaxSkills)
            {
                var eligible = available
                    .Where(s => s.ModelInvocable && chosen.All(c => c.Skill != s) && PathsMatch(s, files))
                    .ToList();

                var ranked = Rank(eligible, message, files);
                if (ranked.Count > 0)
                {
                    List<string> picked = null;
                    if (settings.SmartSkills)
                        picked = await JudgeAsync(settings, ranked.Select(r => r.Skill).ToList(), message, ct);

                    if (picked != null)
                    {
                        foreach (var name in picked) Pick(ranked.Select(r => r.Skill).FirstOrDefault(s => Same(s.Name, name)), "the model picked it for this request");
                    }
                    else
                    {
                        // The model could not be asked: trust only clear word matches.
                        foreach (var r in ranked.Where(r => r.Strong).Take(2)) Pick(r.Skill, "matches your request");
                    }
                }
            }

            if (chosen.Count == 0) return plan;

            // 4. Adapt every chosen skill and deliver them as one read-only reference file.
            var budget = Math.Max(2000, settings.SkillBudgetChars);
            var perSkill = Math.Max(1500, budget / chosen.Count);

            foreach (var (skill, why) in chosen)
            {
                // Reference files are only attached when the user asked for the skill by name.
                var adapted = Adapt(skill, message, workFolder, perSkill, attachReferences: why == "you named it");
                adapted.Why = why;
                plan.Skills.Add(adapted);
            }

            var text = new StringBuilder();
            text.Append("# Skill instructions (reference, do not edit)\n\n");
            if (plan.Skills.Any(a => a.Unsupported.Count > 0)) text.Append(Environment_Note).Append("\n\n");
            foreach (var adapted in plan.Skills) text.Append(adapted.Text).Append("\n\n");

            Directory.CreateDirectory(stateFolder);
            plan.ContextFile = Path.Combine(stateFolder, "skills-active.md");
            File.WriteAllText(plan.ContextFile, text.ToString(), new UTF8Encoding(false));
            plan.Tokens = text.Length / 4;

            var names = string.Join(", ", plan.Skills.Select(a => a.Skill.Name));
            var request = plan.Message.Trim().Length > 0 ? plan.Message : "Apply the " + plan.Skills[0].Skill.Name + " skill to the files in the chat.";
            plan.Message = request + "\n\n(Follow skills-active.md.)";
            plan.Report = BuildReport(plan);
            return plan;
        }

        static string BuildReport(SkillPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("*Skills used (about ").Append(Tokens(plan.Tokens)).Append(" tokens):* ")
              .Append(string.Join(", ", plan.Skills.Select(a => "`" + a.Skill.Name + "` (" + a.Why + ")"))).Append('\n');

            foreach (var a in plan.Skills.Where(a => a.Unsupported.Count > 0))
                sb.Append("*`").Append(a.Skill.Name).Append("`: Aider cannot ").Append(string.Join("; ", a.Unsupported)).Append(".*\n");
            foreach (var a in plan.Skills.Where(a => a.Attached.Count > 0))
                sb.Append("*`").Append(a.Skill.Name).Append("`: included ").Append(string.Join(", ", a.Attached)).Append(".*\n");

            return sb.Append('\n').ToString();
        }

        static string Tokens(int n) => n >= 1000 ? (n / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k" : n.ToString();

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ---- Naming a skill ----------------------------------------------------

        /// <summary>Removes "/name" (at the start) and "$name" (anywhere) when they name a known skill.</summary>
        public static string ExtractNamedSkills(IList<Skill> skills, string text, out List<Skill> named)
        {
            var found = new List<Skill>();
            var rest = text ?? "";

            // /name /other the rest of the message
            while (true)
            {
                var m = Regex.Match(rest, @"^\s*/([A-Za-z0-9][\w\-]*)(\s+|$)");
                if (!m.Success) break;
                var skill = skills.FirstOrDefault(s => Same(s.Name, m.Groups[1].Value));
                if (skill == null) break;
                if (!found.Contains(skill)) found.Add(skill);
                rest = rest.Substring(m.Length);
            }

            // $name anywhere
            rest = Regex.Replace(rest, @"(?<![\w$])\$([A-Za-z0-9][\w\-]*)", m =>
            {
                var skill = skills.FirstOrDefault(s => Same(s.Name, m.Groups[1].Value));
                if (skill == null) return m.Value;
                if (!found.Contains(skill)) found.Add(skill);
                return "";
            });

            named = found;
            return Regex.Replace(rest, @"[ \t]{2,}", " ").Trim();
        }

        // ---- Matching a request to descriptions --------------------------------

        static readonly HashSet<string> Stop = new HashSet<string>(
            ("the and for with this that from into your you our their how what which when then also not but use used using user need needs " +
             "want make create add any all can should will skill skills are was were has have had its them they there here more most some " +
             "such than does done get set new one two about over only just like very each other").Split(' '));

        static readonly Dictionary<string, string> ExtensionWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "csharp dotnet", ["razor"] = "blazor razor component", ["xaml"] = "xaml wpf", ["csproj"] = "dotnet msbuild",
            ["sql"] = "sql database", ["py"] = "python", ["js"] = "javascript", ["ts"] = "typescript", ["html"] = "html",
            ["css"] = "css style", ["json"] = "json", ["md"] = "markdown", ["ps1"] = "powershell", ["bat"] = "batch script"
        };

        static IEnumerable<string> Words(string text)
        {
            foreach (Match m in Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z0-9#+]+"))
            {
                var word = m.Value;
                if (word.Length < 3 || Stop.Contains(word)) continue;

                if (word.EndsWith("ing") && word.Length > 5) word = word.Substring(0, word.Length - 3);
                else if (word.EndsWith("ed") && word.Length > 4) word = word.Substring(0, word.Length - 2);
                else if (word.EndsWith("s") && !word.EndsWith("ss") && word.Length > 3) word = word.Substring(0, word.Length - 1);
                yield return word;
            }
        }

        public sealed class Ranked
        {
            public Skill Skill;
            public double Score;
            public bool Strong;
        }

        /// <summary>Scores every skill against the request (word overlap weighted by rarity). Up to 5 candidates.</summary>
        public static List<Ranked> Rank(List<Skill> skills, string request, IEnumerable<string> contextFiles)
        {
            var query = new Dictionary<string, double>();
            foreach (var word in Words(request)) query[word] = 1.0;

            foreach (var file in contextFiles ?? Enumerable.Empty<string>())
            {
                var ext = Path.GetExtension(file).TrimStart('.');
                if (ExtensionWords.TryGetValue(ext, out var mapped))
                    foreach (var word in Words(mapped))
                        if (!query.ContainsKey(word)) query[word] = 0.7;
            }

            if (query.Count == 0 || skills.Count == 0) return new List<Ranked>();

            var docs = skills.Select(s => new
            {
                Skill = s,
                Name = new HashSet<string>(Words(s.Name.Replace('-', ' '))),
                Description = new HashSet<string>(Words(s.Description))
            }).ToList();

            var df = new Dictionary<string, int>();
            foreach (var d in docs)
                foreach (var w in d.Name.Union(d.Description))
                    df[w] = df.TryGetValue(w, out var n) ? n + 1 : 1;

            var lower = (request ?? "").ToLowerInvariant();
            var result = new List<Ranked>();

            foreach (var d in docs)
            {
                double score = 0;
                var matched = 0;
                var nameHit = false;

                foreach (var q in query)
                {
                    var idf = Math.Log(1 + (double)docs.Count / (df.TryGetValue(q.Key, out var c) ? c : 1));
                    if (d.Name.Contains(q.Key)) { score += 3 * idf * q.Value; matched++; nameHit = true; }
                    else if (d.Description.Contains(q.Key)) { score += idf * q.Value; matched++; }
                }

                // A phrase quoted in the description ("document this app") is a direct trigger.
                var phraseHit = false;
                foreach (Match m in Regex.Matches(d.Skill.Description, "[\"“']([^\"”']{4,60})[\"”']"))
                    if (lower.Contains(m.Groups[1].Value.ToLowerInvariant())) { score += 4; phraseHit = true; }

                if (score >= 1.5 && (matched >= 2 || nameHit || phraseHit))
                    result.Add(new Ranked { Skill = d.Skill, Score = score, Strong = phraseHit || nameHit || matched >= 3 });
            }

            return result.OrderByDescending(r => r.Score).Take(5).ToList();
        }

        /// <summary>Skills with a "paths" list only apply when a matching file is in the chat.</summary>
        static bool PathsMatch(Skill skill, List<string> files)
        {
            if (skill.Paths.Count == 0) return true;

            foreach (var glob in skill.Paths)
            {
                var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
                    .Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*").Replace(@"\?", ".") + "$";
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                foreach (var file in files)
                {
                    var normal = file.Replace('\\', '/');
                    if (regex.IsMatch(normal) || regex.IsMatch(Path.GetFileName(file))) return true;
                    if (!glob.Contains("/") && normal.EndsWith("/" + glob.TrimStart('*'), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>Asks the local model which of a few candidate skills really fit. Returns null if it cannot be asked.</summary>
        static async Task<List<string>> JudgeAsync(AiderSettings settings, List<Skill> candidates, string request, CancellationToken ct)
        {
            if (!settings.Model.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)) return null;
            var model = settings.Model.Substring(settings.Model.IndexOf('/') + 1);

            var list = new StringBuilder();
            foreach (var skill in candidates)
            {
                var description = skill.Description.Length > 220 ? skill.Description.Substring(0, 220) + "..." : skill.Description;
                list.Append("- ").Append(skill.Name).Append(": ").Append(description).Append('\n');
            }

            var body = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["format"] = "json",
                // The same context size as the chat, so Ollama keeps the model loaded instead of reloading it for this call.
                ["keep_alive"] = "10m",
                ["options"] = new JObject { ["temperature"] = 0, ["num_ctx"] = settings.NumCtx, ["num_predict"] = 60 },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "You decide which skills help with a coding request. A skill applies only if its description clearly matches the task. " +
                                      "Answer with JSON only: {\"skills\": [\"name\", ...]} with at most 2 names, or {\"skills\": []} when none apply."
                    },
                    new JObject { ["role"] = "user", ["content"] = "Skills:\n" + list + "\nRequest:\n" + request }
                }
            };

            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                    using (var response = await Http.PostAsync(settings.OllamaBase.TrimEnd('/') + "/api/chat", content, timeout.Token))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                        var reply = JObject.Parse((string)json["message"]["content"]);
                        var names = reply["skills"] as JArray;
                        if (names == null) return null;
                        return names.Select(n => (string)n).Where(n => candidates.Any(c => Same(c.Name, n))).ToList();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        // ---- Adapting one skill ------------------------------------------------

        static readonly Regex ResourceRef = new Regex(
            @"(?<![\w/\\.])((?:references|assets|scripts|examples|templates)/[\w\-./]+\.\w{1,6})|\]\(((?!https?:)[^)#\s]+\.(?:md|txt|json|ya?ml|csv|xml|html|cs|razor))\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml", ".html", ".cs", ".razor", ".sql", ".css", ".js", ".ts", ".ini", ".config"
        };

        /// <summary>Translates a skill into reference text for Aider, within a character limit.</summary>
        public static AdaptedSkill Adapt(Skill skill, string arguments, string projectDir, int limit, bool attachReferences = true)
        {
            var adapted = new AdaptedSkill { Skill = skill };

            var body = Compact(ExpandPlaceholders(skill, skill.Body, arguments, projectDir, adapted));

            // Reference files the skill points to are given to Aider directly, because it cannot open them itself.
            var references = new List<string>();
            foreach (Match m in ResourceRef.Matches(body))
            {
                var rel = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace('/', Path.DirectorySeparatorChar);
                if (!references.Contains(rel, StringComparer.OrdinalIgnoreCase)) references.Add(rel);
            }

            var scripts = references.Where(r => r.StartsWith("scripts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
            if (skill.HasScripts && scripts.Count == 0)
                scripts = SafeList(Path.Combine(skill.Folder, "scripts")).Select(f => Path.Combine("scripts", Path.GetFileName(f))).ToList();
            if (scripts.Count > 0)
                adapted.Unsupported.Add("run scripts (" + string.Join(", ", scripts.Select(s => s.Replace('\\', '/')).Take(3)) + "), so it will show the commands for you to run");
            if (skill.AllowedTools.Length > 0) adapted.Unsupported.Add("use tools (" + skill.AllowedTools + ")");
            if (skill.Context == "fork") adapted.Unsupported.Add("run this skill as a separate agent");

            var attachments = new List<(string Path, string Text)>();
            foreach (var rel in references.Where(r => !scripts.Contains(r)).Take(attachReferences ? MaxAttachedFiles : 0))
            {
                var full = Path.GetFullPath(Path.Combine(skill.Folder, rel));
                if (!full.StartsWith(skill.Folder, StringComparison.OrdinalIgnoreCase)) continue;      // stay inside the skill folder
                if (!File.Exists(full) || !TextExtensions.Contains(Path.GetExtension(full))) continue;
                if (full.Equals(skill.Location, StringComparison.OrdinalIgnoreCase)) continue;

                var text = File.ReadAllText(full);
                attachments.Add((rel.Replace('\\', '/'), Trim(text, MaxAttachedChars)));
                adapted.Attached.Add(rel.Replace('\\', '/'));
            }

            // Budget: the instructions come first, references only fill what is left.
            var footer = 200;
            var bodyLimit = Math.Max(800, limit - footer - attachments.Sum(a => Math.Min(a.Text.Length, 800)));
            var sb = new StringBuilder();
            sb.Append("<skill_content name=\"").Append(skill.Name).Append("\">\n");
            sb.Append(Trim(body, bodyLimit)).Append("\n\n");
            // The folder is only useful when the skill points at files inside it.
            if (references.Count > 0) sb.Append("Skill directory: ").Append(skill.Folder).Append('\n');

            var remaining = limit - sb.Length - footer;
            foreach (var (path, text) in attachments)
            {
                if (remaining < 400) { adapted.Attached.Remove(path); continue; }
                var piece = Trim(text, Math.Min(MaxAttachedChars, remaining));
                sb.Append("\n<skill_reference path=\"").Append(path).Append("\">\n").Append(piece).Append("\n</skill_reference>\n");
                remaining -= piece.Length;
            }

            if (scripts.Count > 0)
            {
                sb.Append("\n<skill_scripts note=\"you cannot run these; tell the user the command instead\">\n");
                foreach (var s in scripts.Take(8)) sb.Append("  <file>").Append(s.Replace('\\', '/')).Append("</file>\n");
                sb.Append("</skill_scripts>\n");
            }

            sb.Append("</skill_content>");
            adapted.Text = sb.ToString();
            return adapted;
        }

        /// <summary>Shows what Aider would receive for a skill (used by the View button).</summary>
        public static string Preview(Skill skill, string projectDir, int limit)
        {
            var adapted = Adapt(skill, "", projectDir, limit);
            var notes = new StringBuilder();
            if (adapted.Unsupported.Count > 0) notes.Append("**Aider cannot:** ").Append(string.Join("; ", adapted.Unsupported)).Append("\n\n");
            if (adapted.Attached.Count > 0) notes.Append("**Reference files included:** ").Append(string.Join(", ", adapted.Attached)).Append("\n\n");
            return notes + adapted.Text;
        }

        static IEnumerable<string> SafeList(string folder)
        {
            try { return Directory.Exists(folder) ? Directory.GetFiles(folder) : new string[0]; }
            catch { return new string[0]; }
        }

        // ---- Placeholders and dynamic commands ---------------------------------

        static string ExpandPlaceholders(Skill skill, string body, string arguments, string projectDir, AdaptedSkill adapted)
        {
            arguments = arguments ?? "";
            var args = SplitArguments(arguments);

            // !`command` and ```! blocks: Aider cannot run commands, so say so instead of leaving a mystery.
            var commands = 0;
            body = Regex.Replace(body, @"(?<=^|\s)!`([^`\n]+)`", m =>
            {
                commands++;
                return "[command not run: " + m.Groups[1].Value + "]";
            }, RegexOptions.Multiline);
            body = Regex.Replace(body, @"```!\s*\n", m =>
            {
                commands++;
                return "(the commands in this block were not run)\n```\n";
            });
            if (commands > 0) adapted.Unsupported.Add("run the " + commands + " command(s) written as !`...`");

            body = body.Replace("${CLAUDE_SKILL_DIR}", skill.Folder)
                       .Replace("${CLAUDE_PROJECT_DIR}", projectDir ?? "")
                       .Replace("${CLAUDE_SESSION_ID}", "aidervs");

            // $ARGUMENTS[N], $N, then named arguments, then $ARGUMENTS
            body = Regex.Replace(body, @"\$ARGUMENTS\[(\d+)\]", m => { return Arg(args, int.Parse(m.Groups[1].Value)); });
            body = Regex.Replace(body, @"(?<![\w$\\])\$(\d)\b", m => { return Arg(args, int.Parse(m.Groups[1].Value)); });
            for (var i = 0; i < skill.ArgumentNames.Count; i++)
            {
                var index = i;
                body = Regex.Replace(body, @"(?<![\w$\\])\$" + Regex.Escape(skill.ArgumentNames[i]) + @"\b", m => { return Arg(args, index); });
            }
            body = body.Replace("$ARGUMENTS", arguments);

            // No "ARGUMENTS:" line when the skill has no placeholder: the request already reaches Aider in the message itself.
            return body.Replace("\\$", "$");
        }

        static string Arg(List<string> args, int index) => index < args.Count ? args[index] : "";

        /// <summary>Splits on spaces but keeps "quoted phrases" together, like a shell.</summary>
        static List<string> SplitArguments(string text)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(text, "\"([^\"]*)\"|'([^']*)'|(\\S+)"))
                list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            return list;
        }

        /// <summary>Drops what the model does not need: HTML comments, trailing spaces and runs of blank lines.</summary>
        static string Compact(string text)
        {
            text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"[ \t]+(?=\n)", "");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        /// <summary>Cuts text at a line boundary and says how much was left out.</summary>
        static string Trim(string text, int limit)
        {
            if (text.Length <= limit) return text;

            var cut = text.LastIndexOf('\n', Math.Max(0, limit - 1));
            if (cut < limit / 2) cut = limit;
            var omitted = text.Substring(cut).Split('\n').Length - 1;
            return text.Substring(0, cut).TrimEnd() + "\n[... " + omitted + " more lines left out to save space ...]";
        }
    }

    /// <summary>A line in the Skills list.</summary>
    public sealed class SkillRow : Notify
    {
        readonly Action<SkillRow> _changed;
        bool _enabled = true;
        bool _always;

        public SkillRow(Skill skill, Action<SkillRow> changed)
        {
            Skill = skill;
            _changed = changed;
        }

        public Skill Skill { get; }
        public string Name => Skill.Name;

        public string Label => Skill.Name + (Skill.NeedsTools ? "  (partly: needs tools)" : "") + (Skill.ModelInvocable ? "" : "  (manual)");

        public string Tooltip =>
            Skill.Description + "\n\n" + Skill.Scope + ": " + Skill.Location +
            (Skill.Warnings.Count > 0 ? "\n\nNotes: " + string.Join("; ", Skill.Warnings) : "");

        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled == value) return; _enabled = value; Raise(nameof(Enabled)); _changed?.Invoke(this); }
        }

        public bool Always
        {
            get => _always;
            set { if (_always == value) return; _always = value; Raise(nameof(Always)); _changed?.Invoke(this); }
        }

        /// <summary>Set without triggering the change callback (used while loading).</summary>
        public void Load(bool enabled, bool always)
        {
            _enabled = enabled;
            _always = always;
        }
    }

    /// <summary>A skill that is active in the current chat.</summary>
    public sealed class SkillChip
    {
        public string Name { get; set; }
    }
}

