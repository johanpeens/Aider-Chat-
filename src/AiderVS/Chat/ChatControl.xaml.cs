using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace AiderVS
{
    /// <summary>One entry in the autocomplete list: a project file (@) or a skill (/ or $).</summary>
    public sealed class FileEntry
    {
        public string Rel { get; set; }       // shown name
        public string Full { get; set; }      // full path (file) or skill name (skill)
        public string Detail { get; set; } = "";
        public bool IsSkill { get; set; }
    }

    public partial class ChatControl : UserControl
    {
        readonly ObservableCollection<ChatMessage> _messages = new ObservableCollection<ChatMessage>();
        readonly ObservableCollection<ContextItem> _context = new ObservableCollection<ContextItem>();
        readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        AiderSettings _settings = AiderSettings.Load();
        SelectionWatcher _watcher;
        DTE2 _dte;
        IdeState _lastIdeState;
        EditorSnippet _snippet;
        CancellationTokenSource _cts;
        ChatMessage _openProposal;
        bool _loadingSettings;

        // autocomplete (@file, /skill, $skill)
        List<FileEntry> _index = new List<FileEntry>();
        string _indexFolder;
        DateTime _indexTime = DateTime.MinValue;
        bool _indexing;
        int _mentionStart = -1;
        char _mentionTrigger;

        public ChatControl()
        {
            InitializeComponent();

            MessageList.ItemsSource = _messages;
            ContextList.ItemsSource = _context;
            _context.CollectionChanged += (s, e) => UpdateContextHeader();

            InitSkills();
            InitSetup();
            WebLog.BeginSession();
            ShowSettings();

            AddInfo("Highlight a file in Solution Explorer (or open one) and ask Aider something. " +
                    "Type @ to pick any file, or / to pick a skill. Edits are shown first and written only when you press Apply.");

            _debounce.Tick += (s, e) => { _debounce.Stop(); FollowIde(); };
            Loaded += OnLoaded;
        }

        // ---- Visual Studio hooks -------------------------------------------------

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_watcher != null) return;

            _dte = IdeContext.GetDte();
            _watcher = new SelectionWatcher();
            _watcher.Changed += (s, args) => { _debounce.Stop(); _debounce.Start(); };

            var ignored1 = ReloadSkillsAsync();
            var ignored2 = FirstRunCheckAsync();
        }

        /// <summary>Called when the selection or active window changes (debounced).</summary>
        void FollowIde()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte == null) return;

            var state = IdeContext.GetActiveState(_dte);
            if (state == null) return; // the chat itself has focus: keep what we had

            _lastIdeState = state;
            if (FollowCheck.IsChecked != true) return;

            // Drop the previous automatic files, keep pinned ones, add the new selection.
            foreach (var old in _context.Where(i => !i.Pinned).ToList()) _context.Remove(old);
            foreach (var path in state.Files) AddFile(path, pinned: false);
        }

        /// <summary>Right-click "Add to Aider chat": pin the file(s) and remember any highlighted code.</summary>
        public void PinFromIde(IdeState state, EditorSnippet snippet)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (state != null)
            {
                _lastIdeState = state;
                foreach (var path in state.Files) AddFile(path, pinned: true, announce: true);
            }
            if (snippet != null) SetSnippet(snippet);
            InputBox.Focus();
        }

        void SaveAllDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Aider reads files from disk, so make sure it sees what is in the editor.
            try { _dte?.Documents.SaveAll(); }
            catch { }
        }

        // ---- Context list --------------------------------------------------------

        /// <summary>Adds a file to the context. When the user chose it, the chat says so with a link to the file.</summary>
        bool AddFile(string path, bool pinned, bool announce = false)
        {
            var existing = _context.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (pinned) existing.Pinned = true;
                return false;
            }

            _context.Add(new ContextItem(path) { Pinned = pinned });
            if (announce) AddInfo("Added " + ShellLinks.Link(Path.GetFileName(path), path) + " to the context.");
            return true;
        }

        void UpdateContextHeader()
        {
            var count = _context.Count;
            ContextExpander.Header = count == 0
                ? "Context: nothing yet"
                : "Context: " + count + (count == 1 ? " file" : " files");
        }

        void LocateContextFile_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkContentElement)sender).DataContext is ContextItem item) ShellLinks.Locate(item.Path);
        }

        void SetSnippet(EditorSnippet snippet)
        {
            _snippet = snippet;
            SnippetLabel.Text = "Code: " + Path.GetFileName(snippet.Path) + " lines " + snippet.StartLine + "-" + snippet.EndLine +
                                " (" + snippet.Text.Length + " chars)";
            SnippetPanel.Visibility = Visibility.Visible;
        }

        void ClearSnippet_Click(object sender, RoutedEventArgs e)
        {
            _snippet = null;
            SnippetPanel.Visibility = Visibility.Collapsed;
        }

        void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is ContextItem item) _context.Remove(item);
        }

        void AddSelected_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_lastIdeState != null)
                foreach (var path in _lastIdeState.Files) AddFile(path, pinned: true, announce: true);
            var snippet = IdeContext.GetEditorSelection(_dte);
            if (snippet != null) SetSnippet(snippet);
        }

        // ---- Autocomplete: @file, /skill, $skill ---------------------------------

        string CurrentFolder() =>
            !string.IsNullOrEmpty(_projectOverride) ? _projectOverride : IdeContext.GetWorkingFolder(_dte, _context.Select(i => i.Path));

        /// <summary>Scans the project folder in the background; the list is reused for a minute.</summary>
        void RefreshIndexIfStale()
        {
            var folder = CurrentFolder();
            if (_indexing || SolutionFiles.IsTooBroad(folder)) return;
            if (folder == _indexFolder && (DateTime.UtcNow - _indexTime).TotalSeconds < 60) return;

            _indexing = true;
            Task.Run(() =>
            {
                var found = SolutionFiles.Enumerate(folder);
                var prefix = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return found.Files
                    .Select(f => new FileEntry
                    {
                        Full = f,
                        Rel = f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? f.Substring(prefix.Length) : f
                    })
                    .ToList();
            }).ContinueWith(task =>
            {
                _indexing = false;
                if (task.Status != TaskStatus.RanToCompletion) return;

                _index = task.Result;
                _indexFolder = folder;
                _indexTime = DateTime.UtcNow;
                UpdatePicker();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingSettings) return;
            UpdatePicker();
        }

        /// <summary>Finds "@file", "$skill" or a leading "/skill" at the caret and shows matches.</summary>
        void UpdatePicker()
        {
            var text = InputBox.Text;
            var caret = Math.Min(InputBox.CaretIndex, text.Length);

            var start = -1;
            var trigger = '\0';
            for (var i = caret - 1; i >= 0; i--)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) break;
                if (c == '@' || c == '$' || (c == '/' && i == 0))
                {
                    if (i == 0 || char.IsWhiteSpace(text[i - 1])) { start = i; trigger = c; }
                    break;
                }
            }

            if (start < 0)
            {
                _mentionStart = -1;
                FilePopup.IsOpen = false;
                return;
            }

            var query = text.Substring(start + 1, caret - start - 1).ToLowerInvariant();
            List<FileEntry> matches;

            if (trigger == '@')
            {
                RefreshIndexIfStale();
                matches = _index
                    .Select(f => new { Entry = f, Name = Path.GetFileName(f.Rel).ToLowerInvariant(), Path = f.Rel.ToLowerInvariant() })
                    .Where(f => query.Length == 0 || f.Path.Contains(query))
                    .OrderBy(f => f.Name.StartsWith(query) ? 0 : f.Name.Contains(query) ? 1 : 2)
                    .ThenBy(f => f.Path.Length)
                    .Take(12)
                    .Select(f => f.Entry)
                    .ToList();
            }
            else
            {
                matches = SkillSuggestions(query);
            }

            if (matches.Count == 0)
            {
                _mentionStart = -1;
                FilePopup.IsOpen = false;
                return;
            }

            _mentionStart = start;
            _mentionTrigger = trigger;
            FileList.ItemsSource = matches;
            FileList.SelectedIndex = 0;
            FilePopup.IsOpen = true;
        }

        /// <summary>Replaces the typed "@query" or "/query" with the choice. Files are also pinned in the context.</summary>
        void AcceptMention()
        {
            var entry = FileList.SelectedItem as FileEntry;
            if (entry == null || _mentionStart < 0) return;

            var caret = Math.Min(InputBox.CaretIndex, InputBox.Text.Length);
            var before = InputBox.Text.Substring(0, _mentionStart);
            var after = InputBox.Text.Substring(caret);

            FilePopup.IsOpen = false;
            var trigger = _mentionTrigger;
            _mentionStart = -1;

            var inserted = entry.IsSkill ? trigger + entry.Rel : entry.Rel;
            InputBox.Text = before + inserted + " " + after;
            InputBox.CaretIndex = before.Length + inserted.Length + 1;

            if (!entry.IsSkill) AddFile(entry.Full, pinned: true, announce: true);
            InputBox.Focus();
        }

        void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptMention();

        // ---- Settings and modes --------------------------------------------------

        void ShowSettings()
        {
            _loadingSettings = true;
            ModelBox.Text = _settings.Model;
            OllamaBox.Text = _settings.OllamaBase;
            CtxBox.Text = _settings.NumCtx.ToString();
            AiderPathBox.Text = _settings.AiderPath;
            ExtraArgsBox.Text = _settings.ExtraArgs;
            AutoCommitCheck.IsChecked = _settings.AutoCommit;
            FollowCheck.IsChecked = _settings.FollowSelection;
            AskOnlyCheck.IsChecked = _settings.AskOnly;
            AutoApplyCheck.IsChecked = _settings.AutoApply;
            AgentCheck.IsChecked = _settings.AgentMode;
            ShowAskOnlyHint();
            ShowSkillSettings();
            _loadingSettings = false;
            UpdateModelButton();
            UpdateWebButton();
        }

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;

            // "Apply edits automatically" and "Agent mode" are two different ways of editing, so ticking one unticks
            // the other. "Ask only" is different: it is a pause switch that overrides editing while it is ticked.
            // It never unticks the other two, so they are exactly as you left them when you untick it again.
            _loadingSettings = true;
            if (sender == AutoApplyCheck && AutoApplyCheck.IsChecked == true) AgentCheck.IsChecked = false;
            else if (sender == AgentCheck && AgentCheck.IsChecked == true) AutoApplyCheck.IsChecked = false;
            _loadingSettings = false;
            ShowAskOnlyHint();

            _settings.FollowSelection = FollowCheck.IsChecked == true;
            _settings.AskOnly = AskOnlyCheck.IsChecked == true;
            _settings.AutoApply = AutoApplyCheck.IsChecked == true;
            _settings.AgentMode = AgentCheck.IsChecked == true;
            _settings.Save();
        }

        /// <summary>While Ask only is on, the two edit modes are dimmed (still clickable) to show they are paused, not lost.</summary>
        void ShowAskOnlyHint()
        {
            var paused = AskOnlyCheck.IsChecked == true;
            AutoApplyCheck.Opacity = paused ? 0.55 : 1;
            AgentCheck.Opacity = paused ? 0.55 : 1;
        }

        void Settings_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;

            _settings.Model = ModelBox.Text.Trim();
            _settings.OllamaBase = OllamaBox.Text.Trim();
            _settings.AiderPath = AiderPathBox.Text.Trim();
            _settings.ExtraArgs = ExtraArgsBox.Text.Trim();
            _settings.AutoCommit = AutoCommitCheck.IsChecked == true;
            if (int.TryParse(CtxBox.Text, out var ctx) && ctx >= 2048) _settings.NumCtx = ctx;
            _settings.Save();
            UpdateModelButton();
        }

        // ---- Chat ----------------------------------------------------------------

        void NewChat_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cts != null) return;

            DiscardOpenProposal("Discarded.");
            WebLog.BeginSession();   // a new chat is a new session in the internet history
            _messages.Clear();
            AiderRunner.ForgetChat(CurrentFolder());
            AddInfo("New chat started. Aider has forgotten the earlier conversation.");
        }

        void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (FilePopup.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        FileList.SelectedIndex = Math.Min(FileList.Items.Count - 1, FileList.SelectedIndex + 1);
                        FileList.ScrollIntoView(FileList.SelectedItem);
                        e.Handled = true;
                        return;
                    case Key.Up:
                        FileList.SelectedIndex = Math.Max(0, FileList.SelectedIndex - 1);
                        FileList.ScrollIntoView(FileList.SelectedItem);
                        e.Handled = true;
                        return;
                    case Key.Enter:
                    case Key.Tab:
                        AcceptMention();
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        FilePopup.IsOpen = false;
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                Send_Click(sender, e);
            }
        }

        async void Send_Click(object sender, RoutedEventArgs e)
        {
            var text = InputBox.Text.Trim();
            if (text.Length == 0 || _cts != null || _askingOverride) return;
            FilePopup.IsOpen = false;

            // Ask only is a pause switch. If the request is clearly about creating or changing files, offer to
            // write them this one time instead of silently answering in words.
            var askOnlyTicked = AskOnlyCheck.IsChecked == true;
            var writeThisTime = askOnlyTicked && WantsFileChanges(text) && await AskEditOverrideAsync();

            var items = _context.ToList();
            var snippet = _snippet ?? (IncludeSelectionCheck.IsChecked == true ? IdeContext.GetEditorSelection(_dte) : null);
            var workFolder = CurrentFolder();
            var settings = _settings;

            var askOnly = askOnlyTicked && !writeThisTime;
            var agent = AgentCheck.IsChecked == true && !askOnly;
            var autoApply = AutoApplyCheck.IsChecked == true;
            // Review mode: Aider works on a copy and nothing reaches the project until the user presses Apply.
            var review = !askOnly && (agent || !autoApply);
            var autoCommit = settings.AutoCommit && !review;

            InputBox.Clear();
            _snippet = null;
            SnippetPanel.Visibility = Visibility.Collapsed;

            DiscardOpenProposal("Discarded because you sent a new message.");

            _messages.Add(new ChatMessage { Role = "You", IsUser = true, Text = Describe(text, items, snippet, agent) });
            var reply = new ChatMessage { Role = "Aider" };
            _messages.Add(reply);
            ScrollToEnd();

            _cts = new CancellationTokenSource();
            SendButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            PendingChange pending = null;
            var runFolder = workFolder;

            try
            {
                if (agent && SolutionFiles.IsTooBroad(workFolder))
                {
                    reply.Text = "Agent mode copies the whole project, and **" + workFolder + "** is too broad to be a project " +
                                 "(a drive, your user folder, Desktop, Documents or Downloads). Open the project's own folder in Visual Studio first.";
                    return;
                }

                SaveAllDocuments();
                var state = AiderRunner.StateFolderFor(workFolder);

                // Internet access (only when the message asks for it): ask permission, show the loader, read, hand Aider the text.
                var web = await RunWebStepAsync(text, state, _cts.Token);
                var request = web?.Message ?? text;

                reply.Text = "Starting...\n";
                var problem = await AiderRunner.EnsureOllamaAsync(settings, _cts.Token);
                if (problem != null)
                {
                    reply.Text = problem + "\n\nPress **Setup** at the top to install or start Ollama.";
                    return;
                }

                // The skill adapter: choose skills, translate them, hand Aider the result as a read-only file.
                SkillPlan plan = null;
                if (_skills != null && _skills.Skills.Count > 0)
                {
                    plan = await SkillAdapter.PlanAsync(_skills, settings, request, items.Select(i => i.Path), null, state, workFolder, _cts.Token);
                }

                var runItems = items;
                if (review)
                {
                    reply.Text = agent
                        ? "Copying the project to a safe sandbox for Aider...\n"
                        : "Preparing a safe copy of your files for Aider...\n";

                    pending = await Task.Run(() => PendingChange.Create(workFolder, state, items, agent));
                    runItems = pending.MappedItems;
                    runFolder = pending.SandboxRoot;
                }

                var askOnlyNote = askOnly && web?.ContextFile != null ? "*Ask only is on: Aider answers from the research and writes nothing.*\n\n" : "";
                reply.Text = (web?.Report ?? "") + askOnlyNote + (plan?.Report ?? "") + (pending?.Warning != null ? pending.Warning + "\n" : "");

                var usedSkills = plan?.ContextFile != null && plan.Skills.Count > 0;
                var message = BuildMessage(usedSkills ? plan.Message : request, snippet);
                // Small models say "I can't browse" out of habit, so spell out that the reading was already done,
                // and that the research is there to carry out the request (create or edit files), not just to talk about.
                var webHint = askOnly
                    ? "(Web pages were already read for you and are in web-pages.md. Answer from them; you do not need to browse.)"
                    : "(Web pages were already read for you and are in web-pages.md. Use them as your research and carry out the request now, including creating or editing files. You do not need to browse.)";
                if (web?.ContextFile != null) message += (usedSkills ? " " : "\n\n") + webHint;

                var extraRead = new System.Collections.Generic.List<string>();
                if (usedSkills) extraRead.Add(plan.ContextFile);
                if (web?.ContextFile != null) extraRead.Add(web.ContextFile);

                var code = await AiderRunner.RunAsync(settings, workFolder, runFolder, message, runItems, autoCommit, askOnly, agent, extraRead, chunk =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        reply.Text += chunk;
                        ScrollToEnd();
                    }));
                }, _cts.Token);

                // The names in Aider's own lines ("Added X to the chat") become links that show the file in File Explorer.
                reply.Text = ShellLinks.Linkify(reply.Text, workFolder, runFolder);

                // The model says it cannot browse and no web step ran: offer to read the web for it.
                if (web?.ContextFile == null && WebReader.LooksLikeBrowseRefusal(reply.Text)) SuggestWebSearch(request);

                var cancelled = _cts.IsCancellationRequested;
                if (code != 0 && !cancelled) reply.Text += "\n[Aider exited with code " + code + "]";
                if (cancelled) reply.Text += "\n[Stopped]";

                if (pending != null && code == 0 && !cancelled)
                {
                    await Task.Run(() => pending.Collect());
                    ShowProposal(reply, pending);
                }
            }
            catch (OperationCanceledException)
            {
                reply.Text += "\n[Stopped]";
            }
            catch (Exception ex)
            {
                reply.Text += "\n[Error] " + ex.Message;
            }
            finally
            {
                // A sandbox that was not handed to a proposal is of no use any more.
                if (pending != null && reply.Pending != pending) pending.Dispose();

                _cts.Dispose();
                _cts = null;
                SendButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ScrollToEnd();
            }
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        // ---- Proposed changes ----------------------------------------------------

        void ShowProposal(ChatMessage reply, PendingChange pending)
        {
            if (pending.Changes.Count == 0)
            {
                reply.Text += "\n\n[Aider proposed no file changes.]";
                return;
            }

            var sb = new StringBuilder();
            sb.Append("\n\n#### Proposed changes\n*Nothing is written to your project until you press Apply.*\n");
            foreach (var change in pending.Changes)
            {
                sb.Append("\n**").Append(change.IsNew ? "New file" : "Changed").Append(":** ")
                  .Append(ShellLinks.Link(change.RelPath, change.OriginalPath))
                  .Append("\n```diff\n").Append(change.DiffText.TrimEnd('\n')).Append("\n```\n");
            }

            reply.Text += sb.ToString();
            reply.Pending = pending;
            reply.ShowActions = true;
            _openProposal = reply;
            UpdateProposalBar();
            ScrollToEnd();
        }

        /// <summary>The bar above the input: visible whenever Aider's changes are waiting for a decision.</summary>
        void UpdateProposalBar()
        {
            var pending = _openProposal?.Pending;
            if (pending == null)
            {
                ProposalBar.Visibility = Visibility.Collapsed;
                return;
            }

            var names = string.Join(", ", pending.Changes.Select(c => Path.GetFileName(c.RelPath)).Take(4));
            if (pending.Changes.Count > 4) names += " and " + (pending.Changes.Count - 4) + " more";
            ProposalText.Text = "Aider proposes changes to " + pending.Changes.Count + (pending.Changes.Count == 1 ? " file" : " files") +
                                " (" + names + "). Nothing is written until you press Apply.";
            ProposalBar.Visibility = Visibility.Visible;
        }

        void ApplyChanges_Click(object sender, RoutedEventArgs e) => ApplyProposal((sender as FrameworkElement)?.Tag as ChatMessage);
        void BarApply_Click(object sender, RoutedEventArgs e) => ApplyProposal(_openProposal);

        void DiscardChanges_Click(object sender, RoutedEventArgs e)
        {
            var message = (sender as FrameworkElement)?.Tag as ChatMessage;
            if (message?.Pending != null) DiscardProposal(message, "Discarded. Your files were not changed.");
        }

        void BarDiscard_Click(object sender, RoutedEventArgs e)
        {
            if (_openProposal != null) DiscardProposal(_openProposal, "Discarded. Your files were not changed.");
        }

        void ApplyProposal(ChatMessage message)
        {
            if (message?.Pending == null) return;

            var pending = message.Pending;
            message.ShowActions = false;
            message.Pending = null;
            if (_openProposal == message) _openProposal = null;
            UpdateProposalBar();

            // The changes are written on a background thread; Visual Studio then reloads the open files.
            Task.Run(() => pending.Apply()).ContinueWith(task =>
            {
                message.Status = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : "Could not apply the changes: " + task.Exception?.GetBaseException().Message;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void DiscardOpenProposal(string reason)
        {
            if (_openProposal != null) DiscardProposal(_openProposal, reason);
        }

        void DiscardProposal(ChatMessage message, string reason)
        {
            message.Pending?.Dispose();
            message.Pending = null;
            message.ShowActions = false;
            message.Status = reason;
            if (_openProposal == message) _openProposal = null;
            UpdateProposalBar();
        }

        // ---- Message text --------------------------------------------------------

        static string BuildMessage(string text, EditorSnippet snippet)
        {
            if (snippet == null) return text;

            var language = Path.GetExtension(snippet.Path).TrimStart('.');
            return text + "\n\nThis is the code I have highlighted in " + Path.GetFileName(snippet.Path) +
                   " (lines " + snippet.StartLine + "-" + snippet.EndLine + "):\n```" + language + "\n" +
                   snippet.Text.TrimEnd() + "\n```\n";
        }

        /// <summary>The user's own bubble: the request, plus links to the files and code that went with it.</summary>
        static string Describe(string text, List<ContextItem> items, EditorSnippet snippet, bool agent)
        {
            var sb = new StringBuilder(text);
            if (items.Count > 0)
                sb.Append("\n\n*Files:* ").Append(string.Join(", ", items.Select(i => ShellLinks.Link(i.Name, i.Path) + (i.ReadOnly ? " (read-only)" : ""))));
            if (snippet != null)
                sb.Append("\n*Code:* ").Append(ShellLinks.Link(Path.GetFileName(snippet.Path), snippet.Path))
                  .Append(" lines ").Append(snippet.StartLine).Append('-').Append(snippet.EndLine);
            if (agent) sb.Append("\n*Agent mode: whole project*");
            return sb.ToString();
        }

        void AddInfo(string text) => _messages.Add(new ChatMessage { Role = "Info", Text = text });

        void ScrollToEnd() => Scroller.ScrollToEnd();
    }
}

