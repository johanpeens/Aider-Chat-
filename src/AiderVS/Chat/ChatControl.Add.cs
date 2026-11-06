using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace AiderVS
{
    /// <summary>The model dropdown beside Send, and the + menu (add files, a folder, open documents, set the project folder).</summary>
    public partial class ChatControl
    {
        // A folder holds many files but the model only remembers a few thousand tokens, so only some are added.
        const int MaxFolderFiles = 20;
        const long MaxFolderBytes = 40 * 1024;

        string _projectOverride;   // set by the user; otherwise the folder Visual Studio has open

        // ---- Model dropdown ------------------------------------------------------

        void UpdateModelButton()
        {
            ModelDropText.Text = ModelTag(_settings.Model);
            ModelDropButton.ToolTip = "Model: " + _settings.Model + "\nClick to pick another installed model or download more.";
        }

        async void ModelDrop_Click(object sender, RoutedEventArgs e)
        {
            var models = await SetupService.ListModelsAsync(_settings);
            ModelDropInfo.Text = models.Count == 0
                ? "No models found. Is Ollama running? Use the button below."
                : "Installed models";

            // fill the list without treating the preselected current model as a click
            ModelDropList.SelectionChanged -= ModelDropList_SelectionChanged;
            ModelDropList.ItemsSource = models;
            ModelDropList.SelectedItem = models.FirstOrDefault(m => SameModel(m.Name, ModelTag(_settings.Model)));
            ModelDropList.SelectionChanged += ModelDropList_SelectionChanged;

            ModelDropPopup.IsOpen = true;
        }

        void ModelDropList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(ModelDropList.SelectedItem is InstalledModel model)) return;

            _settings.Model = "ollama_chat/" + model.Name;
            _settings.Save();

            _loadingSettings = true;
            ModelBox.Text = _settings.Model;
            _loadingSettings = false;

            UpdateModelButton();
            ModelDropPopup.IsOpen = false;
        }

        async void ModelDropSetup_Click(object sender, RoutedEventArgs e)
        {
            ModelDropPopup.IsOpen = false;
            SetupOverlay.Visibility = Visibility.Visible;
            await RecheckAsync();
        }

        // ---- + menu --------------------------------------------------------------

        void AddButton_Click(object sender, RoutedEventArgs e)
        {
            ProjectInfo.Text = "Project folder: " + CurrentFolder() + (_projectOverride != null ? "  (chosen by you)" : "");
            ResetProjectButton.Visibility = _projectOverride != null ? Visibility.Visible : Visibility.Collapsed;
            AddPopup.IsOpen = true;
        }

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;

            var start = CurrentFolder();
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Add files to the Aider context",
                InitialDirectory = Directory.Exists(start) ? start : ""
            };
            if (dialog.ShowDialog() != true) return;

            var left = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                if (!SolutionFiles.IsSourceFile(file)) { left.Add(Path.GetFileName(file)); continue; }
                AddFile(file, pinned: true, announce: true);
            }
            if (left.Count > 0) AddInfo("Left out (too large, or not a text file): " + string.Join(", ", left));
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;

            var folder = BrowseForFolder("Choose a folder to add to the Aider context", CurrentFolder());
            if (folder == null) return;

            var found = SolutionFiles.Enumerate(folder);   // source files only, shallow folders first
            var added = 0;
            var left = 0;
            long bytes = 0;

            foreach (var file in found.Files)
            {
                long length;
                try { length = new FileInfo(file).Length; }
                catch { continue; }

                if (added >= MaxFolderFiles || bytes + length > MaxFolderBytes) { left++; continue; }
                AddFile(file, pinned: true);
                added++;
                bytes += length;
            }

            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            var link = ShellLinks.Link(name.Length > 0 ? name : folder, folder);

            if (added == 0 && left == 0)
                AddInfo("No source files found in " + link + ".");
            else
                AddInfo("Added " + added + (added == 1 ? " file" : " files") + " from " + link +
                        (left > 0 ? ". " + left + " more were left out so everything fits in the model's memory: type @ to pick others, or use Agent mode so Aider finds them itself." : "."));
        }

        void AddOpenDocs_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;
            ThreadHelper.ThrowIfNotOnUIThread();

            var added = 0;
            try
            {
                foreach (EnvDTE.Document document in _dte.Documents)
                {
                    var path = document.FullName;
                    if (File.Exists(path) && SolutionFiles.IsSourceFile(path) && AddFile(path, pinned: true, announce: true)) added++;
                }
            }
            catch { }

            if (added == 0) AddInfo("There are no open documents to add.");
        }

        void SetProject_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;

            var folder = BrowseForFolder("Choose the project folder: where Aider works and where new files go", CurrentFolder());
            if (folder == null) return;

            if (SolutionFiles.IsTooBroad(folder))
            {
                AddInfo("**" + folder + "** is too broad to be a project (a drive, your user folder, Desktop, Documents or Downloads). Pick the project's own folder.");
                return;
            }

            _projectOverride = folder;
            AddInfo("Project folder set to " + ShellLinks.Link(Path.GetFileName(folder.TrimEnd('\\', '/')), folder) + ". Aider works here, and new files go here.");
            AfterProjectChanged();
        }

        void ResetProject_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;
            _projectOverride = null;
            AddInfo("Using the folder Visual Studio has open: " + ShellLinks.Link(Path.GetFileName(CurrentFolder().TrimEnd('\\', '/')), CurrentFolder()) + ".");
            AfterProjectChanged();
        }

        void AfterProjectChanged()
        {
            _indexTime = DateTime.MinValue;           // refresh the @ list for the new folder
            var ignored = ReloadSkillsAsync();        // and the project's own skills
        }

        // ---- Folder picker -------------------------------------------------------

        string BrowseForFolder(string description, string start)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dialog.SelectedPath = start;

                var handle = IntPtr.Zero;
                try
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    object hwnd = _dte.MainWindow.HWnd;          // an int or an IntPtr depending on the SDK
                    handle = hwnd is IntPtr pointer ? pointer : new IntPtr(Convert.ToInt32(hwnd));   // so the dialog opens on top of Visual Studio
                }
                catch { }

                var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new HwndOwner(handle));
                return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        sealed class HwndOwner : System.Windows.Forms.IWin32Window
        {
            public HwndOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
