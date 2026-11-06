using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AiderVS
{
    /// <summary>Clickable file links for the chat: a file shows as a link that opens File Explorer with it selected.</summary>
    public static class ShellLinks
    {
        /// <summary>file:///C:/My%20Folder/a.cs, with brackets escaped so it survives inside a Markdown link.</summary>
        public static string FileUri(string path)
        {
            return new Uri(path).AbsoluteUri.Replace("(", "%28").Replace(")", "%29");
        }

        /// <summary>A Markdown link to a file: [name](file:///...). Falls back to plain text for odd paths.</summary>
        public static string Link(string display, string path)
        {
            try { return "[" + display.Replace("[", "(").Replace("]", ")") + "](" + FileUri(path) + ")"; }
            catch { return display; }
        }

        public static void Open(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (uri.IsFile) Locate(uri.LocalPath);
                else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Shows the file in File Explorer (selected). If it does not exist yet, opens its folder.</summary>
        public static void Locate(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else
                {
                    var parent = Path.GetDirectoryName(path);
                    if (parent != null && Directory.Exists(parent)) Process.Start("explorer.exe", "\"" + parent + "\"");
                }
            }
            catch { }
        }

        /// <summary>
        /// Turns the file names in Aider's own lines ("Added X to the chat.", "Applied edit to X") into links.
        /// Aider prints names relative to the folder it ran in; when it ran in the sandbox copy, point at the real file.
        /// </summary>
        public static string Linkify(string text, string workFolder, string runFolder)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(workFolder)) return text;

            string Resolve(string name)
            {
                try
                {
                    var full = Path.IsPathRooted(name) ? name : Path.Combine(runFolder ?? workFolder, name);
                    full = Path.GetFullPath(full);

                    if (!string.IsNullOrEmpty(runFolder) &&
                        full.StartsWith(runFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        full = Path.Combine(workFolder, full.Substring(runFolder.TrimEnd('\\').Length + 1));
                    return full;
                }
                catch { return null; }
            }

            string Replace(Match m)
            {
                var name = m.Groups[2].Value.Trim();
                var full = Resolve(name);
                if (full == null) return m.Value;
                return m.Groups[1].Value + Link(name, full) + m.Groups[3].Value;
            }

            text = Regex.Replace(text, @"(?m)^(Added )(.+?)( to the chat\.?)[ \t]*$", Replace);
            text = Regex.Replace(text, @"(?m)^(Applied edit to )(.+?)()[ \t]*$", Replace);
            text = Regex.Replace(text, @"(?m)^(Creating empty file )(.+?)()[ \t]*$", Replace);
            return text;
        }
    }
}
