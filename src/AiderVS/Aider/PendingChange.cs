using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AiderVS
{
    public sealed class FileChange
    {
        public string RelPath { get; set; }
        public string OriginalPath { get; set; }
        public string SandboxPath { get; set; }
        public bool IsNew { get; set; }
        public string DiffText { get; set; }
    }

    /// <summary>
    /// Aider never touches your real files in review mode. It works on a copy (the sandbox);
    /// afterwards we compare the copy with the originals, show the differences, and only write
    /// them to your solution when you press Apply.
    /// </summary>
    public sealed class PendingChange : IDisposable
    {
        sealed class Baseline
        {
            public string OriginalPath;
            public string Hash;
        }

        readonly string _workFolder;
        readonly string _sandbox;
        readonly Dictionary<string, Baseline> _baseline = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);

        PendingChange(string workFolder, string sandbox)
        {
            _workFolder = Path.GetFullPath(workFolder);
            _sandbox = sandbox;
        }

        public string SandboxRoot => _sandbox;
        public List<ContextItem> MappedItems { get; } = new List<ContextItem>();
        public List<FileChange> Changes { get; } = new List<FileChange>();
        public string Warning { get; private set; }
        public bool HasGit { get; private set; }

        /// <summary>
        /// Copies the context files (or, in agent mode, the whole filtered solution) into a sandbox folder.
        /// Slow for big solutions, so call it off the UI thread.
        /// </summary>
        public static PendingChange Create(string workFolder, string stateFolder, IEnumerable<ContextItem> items, bool wholeSolution)
        {
            var change = new PendingChange(workFolder, Path.Combine(stateFolder, "sandbox"));
            change.Build(items, wholeSolution);
            return change;
        }

        void Build(IEnumerable<ContextItem> items, bool wholeSolution)
        {
            SafeDelete(_sandbox);
            Directory.CreateDirectory(_sandbox);

            if (wholeSolution)
            {
                var found = SolutionFiles.Enumerate(_workFolder);
                foreach (var file in found.Files)
                {
                    var rel = RelativeUnder(file);
                    if (rel != null) CopyIn(file, rel);
                }
                if (found.Truncated)
                    Warning = "[The solution is large: Aider can only see the first " + found.Files.Count + " source files.]";
            }

            var external = 0;
            foreach (var item in items)
            {
                var rel = RelativeUnder(item.Path) ?? Path.Combine("_external", (++external) + "_" + Path.GetFileName(item.Path));
                if (!_baseline.ContainsKey(rel)) CopyIn(item.Path, rel);
                MappedItems.Add(new ContextItem(Path.Combine(_sandbox, rel)) { ReadOnly = item.ReadOnly });
            }

            // A git repository lets Aider build its map of the solution, so it can find and add files itself.
            if (wholeSolution) HasGit = InitGit();
        }

        void CopyIn(string original, string rel)
        {
            var target = Path.Combine(_sandbox, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var bytes = File.ReadAllBytes(original);
            File.WriteAllBytes(target, bytes);
            _baseline[rel] = new Baseline { OriginalPath = original, Hash = Sha1(bytes) };
        }

        string RelativeUnder(string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = _workFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : null;
        }

        // ---- Comparing ------------------------------------------------------------

        /// <summary>Finds every file Aider changed or created in the sandbox and builds a diff for each.</summary>
        public void Collect()
        {
            Changes.Clear();

            foreach (var file in EnumerateSandbox(_sandbox))
            {
                var rel = file.Substring(_sandbox.Length).TrimStart('\\', '/');
                var bytes = File.ReadAllBytes(file);

                if (_baseline.TryGetValue(rel, out var baseline))
                {
                    if (Sha1(bytes) == baseline.Hash) continue;

                    Changes.Add(new FileChange
                    {
                        RelPath = rel,
                        OriginalPath = baseline.OriginalPath,
                        SandboxPath = file,
                        DiffText = DiffBuilder.Unified(rel, Decode(File.ReadAllBytes(baseline.OriginalPath)), Decode(bytes))
                    });
                }
                else if (!rel.StartsWith("_external", StringComparison.OrdinalIgnoreCase))
                {
                    Changes.Add(new FileChange
                    {
                        RelPath = rel,
                        OriginalPath = Path.Combine(_workFolder, rel),
                        SandboxPath = file,
                        IsNew = true,
                        DiffText = DiffBuilder.NewFile(rel, Decode(bytes))
                    });
                }
            }
        }

        static IEnumerable<string> EnumerateSandbox(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                foreach (var file in Directory.GetFiles(folder))
                    if (!Path.GetFileName(file).StartsWith(".aider", StringComparison.OrdinalIgnoreCase))
                        yield return file;

                foreach (var sub in Directory.GetDirectories(folder))
                {
                    var name = Path.GetFileName(sub);
                    if (name == ".git" || name.StartsWith(".aider", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(sub);
                }
            }
        }

        // ---- Applying -------------------------------------------------------------

        /// <summary>Writes the proposed changes into the real solution and removes the sandbox.</summary>
        public string Apply()
        {
            var written = new List<string>();
            var problems = new List<string>();

            foreach (var change in Changes)
            {
                try
                {
                    if (change.IsNew)
                    {
                        if (File.Exists(change.OriginalPath))
                        {
                            problems.Add(change.RelPath + " already exists, skipped");
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(change.OriginalPath));
                        File.Copy(change.SandboxPath, change.OriginalPath);
                    }
                    else
                    {
                        if (Sha1(File.ReadAllBytes(change.OriginalPath)) != _baseline[change.RelPath].Hash)
                        {
                            problems.Add(change.RelPath + " was changed after the request, skipped");
                            continue;
                        }
                        File.Copy(change.SandboxPath, change.OriginalPath, true);
                    }
                    written.Add(change.RelPath);
                }
                catch (Exception ex)
                {
                    problems.Add(change.RelPath + ": " + ex.Message);
                }
            }

            Dispose();

            var summary = new StringBuilder();
            summary.Append("Applied ").Append(written.Count).Append(written.Count == 1 ? " file" : " files");
            if (written.Count > 0)
            {
                // each file is a link that shows it in File Explorer
                var links = Changes.Where(c => written.Contains(c.RelPath)).Select(c => ShellLinks.Link(c.RelPath, c.OriginalPath));
                summary.Append(": ").Append(string.Join(", ", links));
            }
            if (problems.Count > 0) summary.Append("\nNot applied: ").Append(string.Join("; ", problems));
            return summary.ToString();
        }

        public void Dispose() => SafeDelete(_sandbox);

        // ---- Helpers --------------------------------------------------------------

        static void SafeDelete(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal); // git marks its objects read-only
                Directory.Delete(folder, true);
            }
            catch { /* a leftover sandbox is wiped on the next run */ }
        }

        static string Sha1(byte[] bytes)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes));
        }

        static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

        bool InitGit()
        {
            return RunGit("init -q")
                && RunGit("add -A")
                && RunGit("-c user.name=AiderVS -c user.email=aider@localhost -c commit.gpgsign=false commit -q -m baseline");
        }

        bool RunGit(string arguments)
        {
            foreach (var exe in GitCandidates())
            {
                try
                {
                    var start = new ProcessStartInfo(exe, "-c core.autocrlf=false -c core.safecrlf=false " + arguments)
                    {
                        WorkingDirectory = _sandbox,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var process = Process.Start(start))
                    {
                        process.StandardOutput.ReadToEnd();
                        process.StandardError.ReadToEnd();
                        if (!process.WaitForExit(120000)) { try { process.Kill(); } catch { } return false; }
                        return process.ExitCode == 0;
                    }
                }
                catch (System.ComponentModel.Win32Exception) { /* try the next location */ }
                catch { return false; }
            }
            return false;
        }

        static IEnumerable<string> GitCandidates()
        {
            yield return "git";
            var vs = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (!string.IsNullOrEmpty(vs))
                yield return Path.Combine(vs, @"Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe");
            yield return @"C:\Program Files\Git\cmd\git.exe";
        }
    }

    /// <summary>Plain unified diffs (context of 3 lines) for showing proposed changes in the chat.</summary>
    public static class DiffBuilder
    {
        const int Context = 3;
        const int MaxLines = 400;

        struct Op
        {
            public char Kind;   // ' ', '-', '+'
            public string Text;
            public int A;       // lines of the old file consumed before this operation
            public int B;       // lines of the new file consumed before this operation
        }

        public static string NewFile(string name, string text)
        {
            var lines = Lines(text);
            var sb = new StringBuilder();
            sb.Append("--- /dev/null\n+++ b/").Append(name.Replace('\\', '/')).Append('\n');
            sb.Append("@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");

            for (var i = 0; i < lines.Length && i < MaxLines; i++) sb.Append('+').Append(lines[i]).Append('\n');
            if (lines.Length > MaxLines) sb.Append("... ").Append(lines.Length - MaxLines).Append(" more lines\n");
            return sb.ToString();
        }

        public static string Unified(string name, string before, string after)
        {
            var ops = Compute(Lines(before), Lines(after));

            var changes = new List<int>();
            for (var i = 0; i < ops.Count; i++)
                if (ops[i].Kind != ' ') changes.Add(i);

            var path = name.Replace('\\', '/');
            var sb = new StringBuilder();
            sb.Append("--- a/").Append(path).Append("\n+++ b/").Append(path).Append('\n');

            if (changes.Count == 0)
            {
                sb.Append("(only line endings or the byte-order mark differ)\n");
                return sb.ToString();
            }

            var written = 0;
            var g = 0;
            while (g < changes.Count)
            {
                var first = changes[g];
                var last = first;
                while (g + 1 < changes.Count && changes[g + 1] - last <= 2 * Context + 1)
                {
                    g++;
                    last = changes[g];
                }
                g++;

                var from = Math.Max(0, first - Context);
                var to = Math.Min(ops.Count - 1, last + Context);

                int aCount = 0, bCount = 0;
                for (var i = from; i <= to; i++)
                {
                    if (ops[i].Kind != '+') aCount++;
                    if (ops[i].Kind != '-') bCount++;
                }

                var aStart = aCount == 0 ? ops[from].A : ops[from].A + 1;
                var bStart = bCount == 0 ? ops[from].B : ops[from].B + 1;
                sb.Append("@@ -").Append(aStart).Append(',').Append(aCount)
                  .Append(" +").Append(bStart).Append(',').Append(bCount).Append(" @@\n");

                for (var i = from; i <= to; i++)
                {
                    if (written++ >= MaxLines)
                    {
                        sb.Append("... diff shortened\n");
                        return sb.ToString();
                    }
                    sb.Append(ops[i].Kind).Append(ops[i].Text).Append('\n');
                }
            }
            return sb.ToString();
        }

        static string[] Lines(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return lines.Length > 0 && lines[lines.Length - 1].Length == 0
                ? lines.Take(lines.Length - 1).ToArray()
                : lines;
        }

        static List<Op> Compute(string[] a, string[] b)
        {
            var ops = new List<Op>();

            var start = 0;
            while (start < a.Length && start < b.Length && a[start] == b[start]) start++;

            var endA = a.Length;
            var endB = b.Length;
            while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

            for (var i = 0; i < start; i++) ops.Add(new Op { Kind = ' ', Text = a[i] });

            var n = endA - start;
            var m = endB - start;

            if ((long)n * m > 4000000L)
            {
                // Too big for a precise diff: show the middle as one removed and one added block.
                for (var i = 0; i < n; i++) ops.Add(new Op { Kind = '-', Text = a[start + i] });
                for (var j = 0; j < m; j++) ops.Add(new Op { Kind = '+', Text = b[start + j] });
            }
            else
            {
                var lcs = new int[n + 1, m + 1];
                for (var i = n - 1; i >= 0; i--)
                    for (var j = m - 1; j >= 0; j--)
                        lcs[i, j] = a[start + i] == b[start + j]
                            ? lcs[i + 1, j + 1] + 1
                            : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

                int x = 0, y = 0;
                while (x < n && y < m)
                {
                    if (a[start + x] == b[start + y]) { ops.Add(new Op { Kind = ' ', Text = a[start + x] }); x++; y++; }
                    else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(new Op { Kind = '-', Text = a[start + x] }); x++; }
                    else { ops.Add(new Op { Kind = '+', Text = b[start + y] }); y++; }
                }
                while (x < n) { ops.Add(new Op { Kind = '-', Text = a[start + x] }); x++; }
                while (y < m) { ops.Add(new Op { Kind = '+', Text = b[start + y] }); y++; }
            }

            for (var i = endA; i < a.Length; i++) ops.Add(new Op { Kind = ' ', Text = a[i] });

            // Number the operations so hunk headers can show line positions.
            int lineA = 0, lineB = 0;
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                op.A = lineA;
                op.B = lineB;
                ops[i] = op;
                if (op.Kind != '+') lineA++;
                if (op.Kind != '-') lineB++;
            }
            return ops;
        }
    }
}
