using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AiderVS
{
    public sealed class InstalledModel
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public string SizeText => SetupService.Megabytes(Size);
    }

    public sealed class ModelChoice
    {
        public string Tag { get; set; }
        public string Size { get; set; }
        public string Note { get; set; }
        public double MinRamGb { get; set; }   // comfortable amount of memory
    }

    public sealed class SetupStatus
    {
        public bool OllamaInstalled { get; set; }
        public string OllamaPath { get; set; } = "";
        public bool OllamaRunning { get; set; }
        public string OllamaVersion { get; set; } = "";

        public string UvPath { get; set; } = "";
        public string AiderPath { get; set; } = "";
        public string AiderVersion { get; set; } = "";
        public bool GitFound { get; set; }

        public List<InstalledModel> Models { get; set; } = new List<InstalledModel>();
        public double RamGb { get; set; }

        public bool UvFound => UvPath.Length > 0;
        public bool AiderOk => AiderPath.Length > 0 && AiderVersion.Length > 0;
    }

    /// <summary>
    /// Everything the setup wizard needs: what is installed, what to download, and how. Nothing here runs
    /// by itself: downloads and installs start only when the user presses a button.
    /// </summary>
    public static class SetupService
    {
        // Sizes are the download sizes listed on ollama.com/library.
        public static readonly ModelChoice[] Choices =
        {
            new ModelChoice { Tag = "qwen2.5-coder:1.5b", Size = "986 MB", MinRamGb = 4,  Note = "Very light and fast, but simple answers only. For old PCs." },
            new ModelChoice { Tag = "qwen2.5-coder:3b",   Size = "1.9 GB", MinRamGb = 6,  Note = "Small and quick. Fine for short edits." },
            new ModelChoice { Tag = "qwen2.5-coder:7b",   Size = "4.7 GB", MinRamGb = 12, Note = "The balanced choice for most PCs." },
            new ModelChoice { Tag = "qwen2.5-coder:14b",  Size = "9.0 GB", MinRamGb = 24, Note = "Noticeably better at multi-file changes. Needs a strong PC." },
            new ModelChoice { Tag = "qwen2.5-coder:32b",  Size = "20 GB",  MinRamGb = 48, Note = "Best quality of this family. Needs 32 GB or more of memory." },
            new ModelChoice { Tag = "qwen3-coder:30b",    Size = "19 GB",  MinRamGb = 48, Note = "Newest family, 256K context, only 3B active parameters." }
        };

        /// <summary>The largest model that fits comfortably in the PC's memory.</summary>
        public static ModelChoice Recommend(double ramGb)
        {
            var best = Choices[0];
            foreach (var choice in Choices.Where(c => !c.Tag.StartsWith("qwen3")))
                if (ramGb >= choice.MinRamGb) best = choice;
            return best;
        }

        static readonly HttpClient Quick = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        static readonly HttpClient Long = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // ---- Looking around -----------------------------------------------------

        public static async Task<SetupStatus> CheckAsync(AiderSettings settings, CancellationToken ct)
        {
            var status = new SetupStatus { RamGb = TotalRamGb() };

            status.OllamaPath = FindExe("ollama",
                Path.Combine(Local(), @"Programs\Ollama\ollama.exe"), @"C:\Program Files\Ollama\ollama.exe");
            status.OllamaInstalled = status.OllamaPath.Length > 0;

            try
            {
                var json = JObject.Parse(await Quick.GetStringAsync(settings.OllamaBase.TrimEnd('/') + "/api/version"));
                status.OllamaRunning = true;
                status.OllamaVersion = (string)json["version"] ?? "";
                status.OllamaInstalled = true;
            }
            catch { status.OllamaRunning = false; }

            if (status.OllamaRunning) status.Models = await ListModelsAsync(settings);

            status.UvPath = FindExe("uv", Path.Combine(Home(), @".local\bin\uv.exe"),
                Path.Combine(Local(), @"Microsoft\WinGet\Links\uv.exe"), Path.Combine(Home(), @".cargo\bin\uv.exe"));

            status.AiderPath = FindExe("aider", settings.AiderPath, Path.Combine(Home(), @".local\bin\aider.exe"));
            if (status.AiderPath.Length > 0)
                status.AiderVersion = await Task.Run(() => FirstLine(Capture(status.AiderPath, "--version", 30000)), ct);

            status.GitFound = FindGit().Length > 0;
            return status;
        }

        public static async Task<List<InstalledModel>> ListModelsAsync(AiderSettings settings)
        {
            var list = new List<InstalledModel>();
            try
            {
                var json = JObject.Parse(await Quick.GetStringAsync(settings.OllamaBase.TrimEnd('/') + "/api/tags"));
                foreach (var m in (JArray)json["models"] ?? new JArray())
                    list.Add(new InstalledModel { Name = (string)m["name"], Size = (long?)m["size"] ?? 0 });
            }
            catch { }
            return list.OrderBy(m => m.Name).ToList();
        }

        public static string FindGit()
        {
            return FindExe("git", @"C:\Program Files\Git\cmd\git.exe",
                Path.Combine(Environment.GetEnvironmentVariable("VSINSTALLDIR") ?? "", @"Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe"));
        }

        // ---- Doing things (each one only when the user asks) --------------------

        /// <summary>
        /// Downloads or updates a model. Ollama only fetches the parts that changed, and picks up a stopped
        /// download where it left off. Returns null on success, otherwise the error text.
        /// </summary>
        public static async Task<string> PullAsync(AiderSettings settings, string tag, Action<string, double> progress, CancellationToken ct)
        {
            var body = new JObject { ["name"] = tag, ["stream"] = true }.ToString();
            var request = new HttpRequestMessage(HttpMethod.Post, settings.OllamaBase.TrimEnd('/') + "/api/pull")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            try
            {
                using (var response = await Long.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return "Ollama refused the download (" + (int)response.StatusCode + "): " + await response.Content.ReadAsStringAsync();

                    using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync()))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (line.Length == 0) continue;

                            var json = JObject.Parse(line);
                            if (json["error"] != null) return (string)json["error"];

                            var status = (string)json["status"] ?? "";
                            var total = (long?)json["total"] ?? 0;
                            var done = (long?)json["completed"] ?? 0;

                            if (total > 0)
                                progress(status.StartsWith("pulling") ? "Downloading " + Megabytes(done) + " of " + Megabytes(total) : status, (double)done / total);
                            else
                                progress(status, -1);
                        }
                    }
                }
                return null;
            }
            catch (OperationCanceledException) { return "Stopped. Press Download again to carry on from where it stopped."; }
            catch (Exception ex) { return ex.Message; }
        }

        public static void StartOllama(string ollamaPath)
        {
            Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(ollamaPath) ? "ollama" : ollamaPath, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        public static Task<int> InstallOllamaAsync(Action<string> log, CancellationToken ct) =>
            RunAsync("winget", "install --id Ollama.Ollama -e --accept-package-agreements --accept-source-agreements", log, ct);

        public static Task<int> InstallUvAsync(Action<string> log, CancellationToken ct) =>
            RunAsync("winget", "install --id astral-sh.uv -e --accept-package-agreements --accept-source-agreements", log, ct);

        public static Task<int> InstallAiderAsync(string uv, Action<string> log, CancellationToken ct) =>
            RunAsync(uv, "tool install --python 3.12 --force aider-chat", log, ct);

        public static Task<int> UpgradeAiderAsync(string uv, Action<string> log, CancellationToken ct) =>
            RunAsync(uv, "tool upgrade aider-chat", log, ct);

        /// <summary>Runs a program and reports each output line. Returns the exit code, or -1 if it could not start.</summary>
        public static async Task<int> RunAsync(string exe, string arguments, Action<string> log, CancellationToken ct)
        {
            var start = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process process;
            try { process = Process.Start(start); }
            catch (Exception ex)
            {
                log("Could not start " + Path.GetFileName(exe) + ": " + ex.Message);
                return -1;
            }

            using (process)
            using (ct.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
            {
                var pumps = new[] { Pump(process.StandardOutput, log), Pump(process.StandardError, log) };
                await Task.WhenAll(pumps);
                await Task.Run(() => process.WaitForExit());
                return process.ExitCode;
            }
        }

        static async Task Pump(StreamReader reader, Action<string> log)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                // winget and uv redraw progress bars with carriage returns and block characters: keep only readable lines
                var clean = line.Replace("\r", "").Trim();
                if (clean.Length > 1 && clean.Any(char.IsLetterOrDigit)) log(clean);
            }
        }

        // ---- Helpers ------------------------------------------------------------

        static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Local() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string FindExe(string name, params string[] extra)
        {
            foreach (var path in extra ?? new string[0])
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ".exe");
                    if (dir.Trim().Length > 0 && File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return "";
        }

        static string Capture(string exe, string arguments, int timeoutMs)
        {
            try
            {
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs)) { try { process.Kill(); } catch { } return ""; }
                    return output.Result;
                }
            }
            catch { return ""; }
        }

        static string FirstLine(string text) => (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        public static string Megabytes(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (bytes / (double)(1L << 20)).ToString("0") + " MB";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        public static double TotalRamGb()
        {
            try
            {
                var status = new MemoryStatus();
                return GlobalMemoryStatusEx(status) ? status.TotalPhys / (1024.0 * 1024 * 1024) : 8;
            }
            catch { return 8; }
        }
    }

    /// <summary>A model in the wizard's list.</summary>
    public sealed class ModelOption : Notify
    {
        bool _selected;
        bool _installed;
        string _label;

        public string Tag { get; set; }

        public string Label { get => _label; set => Set(ref _label, value); }
        public bool Installed { get => _installed; set => Set(ref _installed, value); }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }
}
