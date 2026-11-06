using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AiderVS
{
    /// <summary>The source files of a solution folder, without build output, packages, binaries or hidden folders.</summary>
    public sealed class SolutionFileList
    {
        public List<string> Files { get; } = new List<string>();
        public bool Truncated { get; set; }
    }

    public static class SolutionFiles
    {
        public const long MaxFileBytes = 1024 * 1024;
        public const long MaxTotalBytes = 150L * 1024 * 1024;
        public const int MaxFiles = 4000;

        static readonly HashSet<string> SkipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", "packages", "TestResults", "artifacts", "Debug", "Release", "AppPackages", "publish"
        };

        static readonly HashSet<string> SkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".zip", ".7z", ".rar", ".nupkg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp",
            ".mp3", ".mp4", ".wav", ".mov", ".avi", ".apk", ".aab", ".pfx", ".snk", ".db", ".sqlite", ".pdf", ".woff", ".woff2",
            ".ttf", ".eot", ".jar", ".class", ".so", ".cache", ".suo", ".user", ".bin", ".dat", ".onnx", ".pt", ".mdf", ".ldf"
        };

        /// <summary>
        /// The project folder for the path Visual Studio reports. For a .sln file that is its folder; when a
        /// plain folder is opened ("Open Folder") the reported path is the folder itself, not a file in it.
        /// </summary>
        public static string FolderFromSolutionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Directory.Exists(path)) return path;
                var folder = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
            }
            catch { return null; }
        }

        /// <summary>True for a file worth giving to a coding model: not a binary type and not huge.</summary>
        public static bool IsSourceFile(string path)
        {
            try
            {
                if (SkipExtensions.Contains(Path.GetExtension(path))) return false;
                var info = new FileInfo(path);
                return info.Exists && info.Length <= MaxFileBytes;
            }
            catch { return false; }
        }

        /// <summary>A drive root, the user profile, Desktop, Documents or Downloads is never a project to copy or scan.</summary>
        public static bool IsTooBroad(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return true;
            try
            {
                var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (full.Length <= 2) return true; // "C:"

                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var broad = new[]
                {
                    profile,
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(profile, "Downloads")
                };
                return broad.Any(b => !string.IsNullOrEmpty(b) &&
                                      string.Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase));
            }
            catch { return true; }
        }

        /// <summary>Walks the folder, shallow folders first, and stops at sensible size limits.</summary>
        public static SolutionFileList Enumerate(string root)
        {
            var result = new SolutionFileList();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            long total = 0;
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var folder = queue.Dequeue();

                string[] files;
                try { files = Directory.GetFiles(folder); }
                catch { continue; }

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".aider", StringComparison.OrdinalIgnoreCase)) continue;
                    if (SkipExtensions.Contains(Path.GetExtension(name))) continue;

                    long length;
                    try { length = new FileInfo(file).Length; }
                    catch { continue; }
                    if (length > MaxFileBytes) continue;

                    if (result.Files.Count >= MaxFiles || total + length > MaxTotalBytes)
                    {
                        result.Truncated = true;
                        return result;
                    }

                    result.Files.Add(file);
                    total += length;
                }

                string[] folders;
                try { folders = Directory.GetDirectories(folder); }
                catch { continue; }

                foreach (var sub in folders)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || SkipFolders.Contains(name)) continue;
                    queue.Enqueue(sub);
                }
            }

            return result;
        }
    }
}
