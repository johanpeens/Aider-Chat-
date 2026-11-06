using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiderVS
{
    /// <summary>One skill: a folder with a SKILL.md (YAML frontmatter plus Markdown instructions).</summary>
    public sealed class Skill
    {
        public string Name { get; set; }
        public string Description { get; set; } = "";
        public string Location { get; set; }          // full path of SKILL.md
        public string Folder { get; set; }            // the skill's own folder
        public string Scope { get; set; }             // project, configured, user or app
        public string Body { get; set; } = "";        // instructions without the frontmatter

        public string ArgumentHint { get; set; } = "";
        public List<string> ArgumentNames { get; } = new List<string>();
        public List<string> Paths { get; } = new List<string>();   // only auto-load for matching files
        public string AllowedTools { get; set; } = "";
        public string Context { get; set; } = "";     // "fork": run the skill as a separate agent
        public bool ModelInvocable { get; set; } = true;   // false: only when the user names it
        public bool UserInvocable { get; set; } = true;    // false: hidden from the / list
        public bool HasScripts { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Skills that rely on running tools or scripts only partly work: Aider cannot run them.</summary>
        public bool NeedsTools => HasScripts || AllowedTools.Length > 0 || Context == "fork";
    }

    /// <summary>Reads SKILL.md leniently: skills written for other clients often bend the YAML rules.</summary>
    public static class SkillParser
    {
        static readonly Regex ValidName = new Regex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
        static readonly Regex KeyLine = new Regex(@"^([A-Za-z_][\w\-]*)\s*:\s*(.*)$", RegexOptions.Compiled);

        public static Skill Parse(string skillFile, string scope)
        {
            var folder = Path.GetDirectoryName(skillFile);
            var folderName = Path.GetFileName(folder);

            var text = File.ReadAllText(skillFile).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');

            var meta = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var body = text;

            if (text.StartsWith("---"))
            {
                var close = Regex.Match(text.Substring(3), @"\n---[ \t]*(\n|$)");
                if (close.Success)
                {
                    var front = text.Substring(3, close.Index);
                    body = text.Substring(3 + close.Index + close.Length);
                    meta = ParseYaml(front);
                }
            }

            var skill = new Skill
            {
                Location = skillFile,
                Folder = folder,
                Scope = scope,
                Body = body.Trim(),
                HasScripts = Directory.Exists(Path.Combine(folder, "scripts"))
            };

            skill.Name = Str(meta, "name").Trim();
            if (skill.Name.Length == 0) skill.Name = folderName;
            else if (!string.Equals(skill.Name, folderName, StringComparison.OrdinalIgnoreCase))
                skill.Warnings.Add("name '" + skill.Name + "' does not match its folder '" + folderName + "'");
            if (!ValidName.IsMatch(skill.Name)) skill.Warnings.Add("name should use lowercase letters, digits and single hyphens");
            if (skill.Name.Length > 64) skill.Warnings.Add("name is longer than 64 characters");

            var description = Str(meta, "description").Trim();
            var when = Str(meta, "when_to_use").Trim();
            if (when.Length > 0) description = (description + " " + when).Trim();
            if (description.Length == 0)
            {
                description = FirstSentence(skill.Body);
                skill.Warnings.Add("no description: using the first line of the instructions");
            }
            if (description.Length > 1024) skill.Warnings.Add("description is longer than 1024 characters");
            skill.Description = description;

            skill.ArgumentHint = Str(meta, "argument-hint");
            skill.AllowedTools = Str(meta, "allowed-tools");
            skill.Context = Str(meta, "context");
            skill.ModelInvocable = !IsTrue(Str(meta, "disable-model-invocation"));
            skill.UserInvocable = !string.Equals(Str(meta, "user-invocable"), "false", StringComparison.OrdinalIgnoreCase);
            skill.ArgumentNames.AddRange(List(meta, "arguments"));
            skill.Paths.AddRange(List(meta, "paths"));

            return skill;
        }

        static string FirstSentence(string body)
        {
            foreach (var line in body.Split('\n'))
            {
                var t = line.Trim().TrimStart('#', ' ');
                if (t.Length > 0) return t.Length > 200 ? t.Substring(0, 200) : t;
            }
            return "";
        }

        static bool IsTrue(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

        static string Str(Dictionary<string, object> meta, string key)
        {
            if (!meta.TryGetValue(key, out var value)) return "";
            if (value is List<string> list) return string.Join(" ", list);
            return value as string ?? "";
        }

        static IEnumerable<string> List(Dictionary<string, object> meta, string key)
        {
            if (!meta.TryGetValue(key, out var value)) return Enumerable.Empty<string>();
            if (value is List<string> list) return list;

            var text = value as string ?? "";
            return text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('"', '\'', '[', ']'));
        }

        /// <summary>
        /// A small YAML reader for frontmatter: plain, quoted and block (&gt; and |) scalars, inline and dashed lists.
        /// A value like "Use this when: the user asks" (a colon inside plain text) is kept whole.
        /// </summary>
        static Dictionary<string, object> ParseYaml(string yaml)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var lines = yaml.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                if (char.IsWhiteSpace(line[0])) continue; // belongs to a value we did not understand

                var match = KeyLine.Match(line);
                if (!match.Success) continue;

                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value.Trim();

                // Block scalar: description: >  or  |
                if (Regex.IsMatch(value, @"^[>|][+-]?$"))
                {
                    var folded = value.StartsWith(">");
                    var parts = new List<string>();
                    while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                        parts.Add(lines[++i].Trim());
                    map[key] = folded ? string.Join(" ", parts.Where(p => p.Length > 0)) : string.Join("\n", parts).Trim();
                    continue;
                }

                // Dashed list or nested map under an empty value
                if (value.Length == 0)
                {
                    var items = new List<string>();
                    var raw = new List<string>();
                    while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || char.IsWhiteSpace(lines[i + 1][0]) || lines[i + 1].StartsWith("- ")))
                    {
                        var next = lines[++i].Trim();
                        if (next.StartsWith("- ")) items.Add(Unquote(next.Substring(2).Trim()));
                        else if (next.Length > 0) raw.Add(next);
                    }
                    if (items.Count > 0) map[key] = items;
                    else map[key] = string.Join(" ", raw);
                    continue;
                }

                // Inline list: [a, b]
                if (value.StartsWith("[") && value.EndsWith("]"))
                {
                    map[key] = value.Substring(1, value.Length - 2).Split(',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();
                    continue;
                }

                // Quoted or plain scalar; a plain scalar may continue on indented lines
                if (value.StartsWith("\"") || value.StartsWith("'"))
                {
                    map[key] = Unquote(value);
                    continue;
                }

                var sb = new StringBuilder(value);
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]) && lines[i + 1].Trim().Length > 0)
                    sb.Append(' ').Append(lines[++i].Trim());
                map[key] = sb.ToString();
            }

            return map;
        }

        static string Unquote(string value)
        {
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                var inner = value.Substring(1, value.Length - 2);
                return value[0] == '"' ? inner.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\") : inner.Replace("''", "'");
            }
            return value;
        }
    }

    /// <summary>Finds the skills on this machine and in the open solution.</summary>
    public sealed class SkillStore
    {
        const int MaxFolders = 2000;
        const int MaxDepth = 3;

        public List<Skill> Skills { get; } = new List<Skill>();
        public List<string> Diagnostics { get; } = new List<string>();

        /// <summary>Skills found in the solution that are not loaded until the user trusts the solution.</summary>
        public List<string> PendingTrust { get; } = new List<string>();

        public Skill Find(string name) =>
            Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        public static SkillStore Load(string workFolder, string extraFolders, ICollection<string> trustedFolders)
        {
            var store = new SkillStore();
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Highest precedence first: when two skills share a name, the first one found wins.
            var trusted = trustedFolders != null && trustedFolders.Any(t => SameFolder(t, workFolder));
            foreach (var sub in new[] { @".aidervs\skills", @".agents\skills", @".claude\skills" })
            {
                var root = Path.Combine(workFolder ?? "", sub);
                if (trusted) store.Scan(root, "project");
                else store.NoteUntrusted(root);
            }

            foreach (var folder in (extraFolders ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                store.Scan(Environment.ExpandEnvironmentVariables(folder.Trim()), "configured");

            store.Scan(Path.Combine(user, ".agents", "skills"), "user");
            store.Scan(Path.Combine(user, ".claude", "skills"), "user");
            store.Scan(Path.Combine(appData, "AiderVS", "skills"), "app");

            store.Skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return store;
        }

        static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        void NoteUntrusted(string root)
        {
            if (!Directory.Exists(root)) return;
            var count = 0;
            try { count = Directory.GetDirectories(root).Count(d => File.Exists(Path.Combine(d, "SKILL.md"))); }
            catch { }
            if (count > 0) PendingTrust.Add(root + " (" + count + (count == 1 ? " skill)" : " skills)"));
        }

        void Scan(string root, string scope)
        {
            if (!Directory.Exists(root)) return;

            var visited = 0;
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0 && visited < MaxFolders)
            {
                var (folder, depth) = queue.Dequeue();
                visited++;

                var file = Path.Combine(folder, "SKILL.md");
                if (File.Exists(file))
                {
                    Add(file, scope);
                    continue; // a skill's own subfolders are resources, not more skills
                }

                if (depth >= MaxDepth) continue;

                string[] children;
                try { children = Directory.GetDirectories(folder); }
                catch { continue; }

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (name == ".git" || name == "node_modules") continue;
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        void Add(string file, string scope)
        {
            Skill skill;
            try { skill = SkillParser.Parse(file, scope); }
            catch (Exception ex)
            {
                Diagnostics.Add("Skipped " + file + ": " + ex.Message);
                return;
            }

            var existing = Find(skill.Name);
            if (existing != null)
            {
                Diagnostics.Add("'" + skill.Name + "' in " + file + " is hidden by " + existing.Location);
                return;
            }

            Skills.Add(skill);
        }
    }
}
