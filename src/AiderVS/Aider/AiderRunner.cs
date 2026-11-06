using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiderVS
{
    /// <summary>
    /// Runs Aider once per chat message (aider --message-file) and streams its output back.
    /// Chat history is kept in a per-solution file so Aider remembers the conversation.
    /// </summary>
    public static class AiderRunner
    {
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        public static string StateFolderFor(string workFolder)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(workFolder.ToLowerInvariant()));
                var id = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
                return Path.Combine(AiderSettings.StateRoot, id);
            }
        }

        public static void ForgetChat(string workFolder)
        {
            try
            {
                var history = Path.Combine(StateFolderFor(workFolder), "chat.history.md");
                if (File.Exists(history)) File.Delete(history);
            }
            catch { }
        }

        /// <summary>Makes sure Ollama answers; starts it if it is local and not running.</summary>
        public static async Task<string> EnsureOllamaAsync(AiderSettings settings, CancellationToken ct)
        {
            if (!settings.Model.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)) return null;

            if (await IsUpAsync(settings.OllamaBase)) return null;

            var uri = new Uri(settings.OllamaBase);
            if (!uri.IsLoopback) return "Ollama is not answering at " + settings.OllamaBase + ".";

            try
            {
                Process.Start(new ProcessStartInfo("ollama", "serve")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
                return "Ollama is not running and could not be started. Install it from ollama.com.";
            }

            for (var i = 0; i < 20; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                if (await IsUpAsync(settings.OllamaBase)) return null;
            }
            return "Ollama did not come up on " + settings.OllamaBase + ".";
        }

        static async Task<bool> IsUpAsync(string baseUrl)
        {
            try
            {
                using (var response = await Http.GetAsync(baseUrl.TrimEnd('/') + "/api/version"))
                    return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public static async Task<int> RunAsync(
            AiderSettings settings,
            string workFolder,
            string runFolder,
            string message,
            IReadOnlyList<ContextItem> items,
            bool autoCommit,
            bool askOnly,
            bool agent,
            IEnumerable<string> extraReadFiles,
            Action<string> onText,
            CancellationToken ct)
        {
            var state = StateFolderFor(workFolder);
            Directory.CreateDirectory(state);

            var messageFile = Path.Combine(state, "message.txt");
            File.WriteAllText(messageFile, message, new UTF8Encoding(false));

            var modelSettingsFile = Path.Combine(state, "model.settings.yml");
            File.WriteAllText(modelSettingsFile,
                "- name: " + settings.Model + "\n" +
                "  extra_params:\n" +
                "    num_ctx: " + settings.NumCtx + "\n");

            var args = new StringBuilder();
            args.Append("--model ").Append(Quote(settings.Model));
            args.Append(" --model-settings-file ").Append(Quote(modelSettingsFile));
            args.Append(" --message-file ").Append(Quote(messageFile));
            args.Append(" --chat-history-file ").Append(Quote(Path.Combine(state, "chat.history.md")));
            args.Append(" --input-history-file ").Append(Quote(Path.Combine(state, "input.history")));
            args.Append(" --restore-chat-history");
            args.Append(" --yes-always --no-pretty --no-stream --no-fancy-input");
            args.Append(" --no-show-model-warnings --no-analytics --no-check-update");
            args.Append(" --no-detect-urls --no-suggest-shell-commands");

            // Ask mode answers in plain markdown and never edits files.
            if (askOnly) args.Append(" --chat-mode ask");

            if (!HasGitRepo(runFolder)) args.Append(" --no-git");
            else
            {
                if (!autoCommit) args.Append(" --no-auto-commits --no-dirty-commits");
                // In agent mode Aider reads a map of the whole solution, so it can pick and add files itself.
                if (agent) args.Append(" --map-tokens 2048 --no-gitignore");
            }

            if (!string.IsNullOrWhiteSpace(settings.ExtraArgs)) args.Append(' ').Append(settings.ExtraArgs);

            foreach (var item in items.Where(i => i.ReadOnly))
                args.Append(" --read ").Append(Quote(item.Path));
            // Skill instructions arrive as read-only reference files, so they never pile up in the chat history.
            foreach (var path in extraReadFiles ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(path)) args.Append(" --read ").Append(Quote(path));
            foreach (var item in items.Where(i => !i.ReadOnly))
                args.Append(' ').Append(Quote(item.Path));

            var start = new ProcessStartInfo
            {
                FileName = ResolveAider(settings),
                Arguments = args.ToString(),
                WorkingDirectory = runFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            start.EnvironmentVariables["NO_COLOR"] = "1";
            // Without a terminal, Aider's console wraps every line at 80 columns; ask for a very wide one.
            start.EnvironmentVariables["COLUMNS"] = "1000";
            start.EnvironmentVariables["OLLAMA_API_BASE"] = settings.OllamaBase;

            using (var process = new Process { StartInfo = start })
            {
                try { process.Start(); }
                catch (Exception ex)
                {
                    onText("Could not start Aider (" + start.FileName + "): " + ex.Message +
                           "\nSet the Aider path under Settings, or run aider-qwen.bat check to install it.\n");
                    return -1;
                }

                process.StandardInput.Close();

                using (ct.Register(() => KillTree(process)))
                {
                    var pumps = new[] { Pump(process.StandardOutput, onText), Pump(process.StandardError, onText) };
                    await Task.WhenAll(pumps);
                    await Task.Run(() => process.WaitForExit());
                    return ct.IsCancellationRequested ? -1 : process.ExitCode;
                }
            }
        }

        static string ResolveAider(AiderSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AiderPath) && File.Exists(settings.AiderPath))
                return settings.AiderPath;

            var uvTool = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "aider.exe");
            return File.Exists(uvTool) ? uvTool : "aider";
        }

        static bool HasGitRepo(string folder)
        {
            for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return true;
            return false;
        }

        static async Task Pump(StreamReader reader, Action<string> sink)
        {
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var text = Ansi.Replace(new string(buffer, 0, read), "");
                if (text.Length > 0) sink(text);
            }
        }

        static void KillTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (var killer = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    killer?.WaitForExit(3000);
                }
            }
            catch { }
        }

        static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
