using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace AiderVS
{
    /// <summary>What the user is looking at in Visual Studio right now.</summary>
    public sealed class IdeState
    {
        public List<string> Files { get; } = new List<string>();
        public bool FromEditor { get; set; }
    }

    /// <summary>Reads Solution Explorer and the editor. Call on the UI thread only.</summary>
    public static class IdeContext
    {
        const string SolutionExplorerKind = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}";
        const long MaxFileBytes = 500 * 1024;

        public static DTE2 GetDte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Package.GetGlobalService(typeof(DTE)) as DTE2;
        }

        /// <summary>
        /// Returns the files behind whatever window is active: the highlighted items in
        /// Solution Explorer, or the file in the active editor tab. Returns null when
        /// another window (like the chat itself) has focus, so the old context stays.
        /// </summary>
        public static IdeState GetActiveState(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte == null) return null;

            Window window;
            try { window = dte.ActiveWindow; } catch { return null; }
            if (window == null) return null;

            var state = new IdeState();

            if (string.Equals(window.ObjectKind, SolutionExplorerKind, StringComparison.OrdinalIgnoreCase))
            {
                AddSolutionExplorerSelection(dte, state.Files);
                return state;
            }

            if (window.Kind == "Document")
            {
                var path = TryGetDocumentPath(dte);
                if (path != null) state.Files.Add(path);
                state.FromEditor = true;
                return state;
            }

            return null;
        }

        /// <summary>The text highlighted in the last active editor tab, or null if nothing is highlighted.</summary>
        public static EditorSnippet GetEditorSelection(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = dte?.ActiveDocument;
                if (doc == null) return null;
                var selection = doc.Selection as TextSelection;
                if (selection == null || string.IsNullOrWhiteSpace(selection.Text)) return null;

                return new EditorSnippet
                {
                    Path = doc.FullName,
                    StartLine = selection.TopLine,
                    EndLine = selection.BottomLine,
                    Text = selection.Text
                };
            }
            catch { return null; }
        }

        public static string GetWorkingFolder(DTE2 dte, IEnumerable<string> contextFiles)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Open Folder mode reports the folder itself, a .sln reports the file: handle both.
                var folder = SolutionFiles.FolderFromSolutionPath(dte?.Solution?.FullName);
                if (folder != null) return folder;
            }
            catch { }

            var first = contextFiles?.FirstOrDefault();
            if (first != null) return Path.GetDirectoryName(first);
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        static string TryGetDocumentPath(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var path = dte.ActiveDocument?.FullName;
                return IsUsable(path) ? path : null;
            }
            catch { return null; }
        }

        static void AddSolutionExplorerSelection(DTE2 dte, List<string> files)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            object[] selected;
            try { selected = dte.ToolWindows.SolutionExplorer.SelectedItems as object[]; }
            catch { return; }
            if (selected == null) return;

            foreach (var entry in selected)
            {
                var item = entry as UIHierarchyItem;
                if (item == null) continue;

                try
                {
                    switch (item.Object)
                    {
                        case ProjectItem projectItem:
                            for (short i = 1; i <= projectItem.FileCount; i++)
                            {
                                var path = projectItem.FileNames[i];
                                if (IsUsable(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase))
                                    files.Add(path);
                            }
                            break;

                        case Project project:
                            var projectFile = project.FullName;
                            if (IsUsable(projectFile) && !files.Contains(projectFile, StringComparer.OrdinalIgnoreCase))
                                files.Add(projectFile);
                            break;
                    }
                }
                catch { /* some nodes (folders, virtual items) have no file */ }
            }
        }

        static bool IsUsable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length <= MaxFileBytes;
            }
            catch { return false; }
        }
    }
}
