using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AiderVS
{
    public sealed class WebLogEntry
    {
        public string Type { get; set; }        // session, page, search, denied, stopped
        public string Session { get; set; }
        public DateTime Time { get; set; }
        public string Folder { get; set; }      // project folder (session entries)
        public string Url { get; set; }
        public string Title { get; set; }
        public string Query { get; set; }
        public string Provider { get; set; }
        public long Bytes { get; set; }
        public int Count { get; set; }          // search results
        public string Error { get; set; }       // empty when it worked
        public bool Remembered { get; set; }    // allowed without asking, because the user ticked "don't ask again"
    }

    /// <summary>
    /// A record of everything read from the internet, grouped by chat session and kept between runs,
    /// so you can always see which pages were accessed. Stored in %LOCALAPPDATA%\AiderVS\web-history.jsonl.
    /// </summary>
    public static class WebLog
    {
        const int MaxLines = 3000;
        static readonly object Gate = new object();
        static string _session;
        static bool _headerWritten;

        static string FilePath => Path.Combine(AiderSettings.StateRoot, "web-history.jsonl");

        /// <summary>A new chat starts a new session. Nothing is written until something is actually read.</summary>
        public static void BeginSession()
        {
            lock (Gate)
            {
                _session = Guid.NewGuid().ToString("N").Substring(0, 8);
                _headerWritten = false;
            }
        }

        // ---- Writing ---------------------------------------------------------------

        public static void LogResult(WebPlan plan, WebResult result, bool remembered, string folder)
        {
            if (plan.IsSearch)
                Append(folder, new WebLogEntry
                {
                    Type = "search",
                    Query = plan.Query,
                    Provider = result.Provider,
                    Count = result.Hits.Count,
                    Remembered = remembered
                });

            foreach (var page in result.Pages)
                Append(folder, new WebLogEntry
                {
                    Type = "page",
                    Url = page.Url,
                    Title = page.Title,
                    Bytes = page.Bytes,
                    Error = page.Error,
                    Remembered = remembered
                });
        }

        public static void LogDenied(WebPlan plan, string folder)
        {
            if (plan.IsSearch) Append(folder, new WebLogEntry { Type = "denied", Query = plan.Query });
            foreach (var url in plan.Urls) Append(folder, new WebLogEntry { Type = "denied", Url = url });
        }

        public static void LogStopped(WebPlan plan, string folder)
        {
            Append(folder, new WebLogEntry { Type = "stopped", Query = plan.IsSearch ? plan.Query : null, Url = plan.Urls.FirstOrDefault() });
        }

        static void Append(string folder, WebLogEntry entry)
        {
            try
            {
                lock (Gate)
                {
                    if (_session == null) BeginSession();
                    Directory.CreateDirectory(AiderSettings.StateRoot);

                    var lines = new List<string>();
                    if (!_headerWritten)
                    {
                        lines.Add(JsonConvert.SerializeObject(new WebLogEntry { Type = "session", Session = _session, Time = DateTime.Now, Folder = folder }));
                        _headerWritten = true;
                    }

                    entry.Session = _session;
                    entry.Time = DateTime.Now;
                    lines.Add(JsonConvert.SerializeObject(entry));
                    File.AppendAllLines(FilePath, lines, Encoding.UTF8);

                    Trim();
                }
            }
            catch { /* a failed log write must never break the chat */ }
        }

        /// <summary>Keeps the file from growing for ever: the oldest lines go first.</summary>
        static void Trim()
        {
            var all = File.ReadAllLines(FilePath);
            if (all.Length > MaxLines) File.WriteAllLines(FilePath, all.Skip(all.Length - MaxLines * 2 / 3), Encoding.UTF8);
        }

        public static void Clear()
        {
            lock (Gate)
            {
                try { if (File.Exists(FilePath)) File.Delete(FilePath); }
                catch { }
                _headerWritten = false;
            }
        }

        // ---- Reading ---------------------------------------------------------------

        public static List<WebLogEntry> Load()
        {
            var list = new List<WebLogEntry>();
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    try
                    {
                        var entry = JsonConvert.DeserializeObject<WebLogEntry>(line);
                        if (entry != null) list.Add(entry);
                    }
                    catch { /* skip a damaged line */ }
                }
            }
            return list;
        }

        /// <summary>The history as Markdown, newest session first, ready for the chat's renderer.</summary>
        public static string ToMarkdown()
        {
            var entries = Load();
            if (entries.Count == 0) return "Nothing has been read from the internet yet.";

            var sb = new StringBuilder();
            var sessions = entries.Where(e => e.Type == "session").OrderByDescending(e => e.Time).ToList();

            foreach (var session in sessions)
            {
                var folder = string.IsNullOrEmpty(session.Folder) ? "no project" : Path.GetFileName(session.Folder.TrimEnd('\\', '/'));
                sb.Append("### ").Append(session.Time.ToString("d MMM yyyy, HH:mm")).Append(" — ").Append(folder);
                if (session.Session == _session) sb.Append(" (this chat)");
                sb.Append('\n');

                foreach (var e in entries.Where(x => x.Session == session.Session && x.Type != "session").OrderBy(x => x.Time))
                    sb.Append("- ").Append(Describe(e)).Append('\n');

                sb.Append('\n');
            }
            return sb.ToString();
        }

        static string Describe(WebLogEntry e)
        {
            var time = e.Time.ToString("HH:mm") + "  ";
            var how = e.Remembered ? " *(allowed earlier)*" : "";

            switch (e.Type)
            {
                case "search":
                    return time + "**Searched** " + (e.Provider ?? "the web") + " for “" + e.Query + "” (" + e.Count + " results)" + how;

                case "page":
                    var name = Clean(string.IsNullOrEmpty(e.Title) ? e.Url : e.Title);
                    if (!string.IsNullOrEmpty(e.Error))
                        return time + "✖ Could not read [" + name + "](" + e.Url + "): " + e.Error + how;
                    return time + "✔ **Read** [" + name + "](" + e.Url + ") (" + (e.Bytes >= 1024 ? e.Bytes / 1024 + " KB" : e.Bytes + " bytes") + ")" + how;

                case "denied":
                    return time + "**Not allowed** (nothing was read): " + (e.Url ?? "search for “" + e.Query + "”");

                case "stopped":
                    return time + "**Stopped** before it finished: " + (e.Url ?? "search for “" + e.Query + "”");

                default:
                    return time + e.Type;
            }
        }

        static string Clean(string s) => (s ?? "").Replace("[", "(").Replace("]", ")");
    }
}
