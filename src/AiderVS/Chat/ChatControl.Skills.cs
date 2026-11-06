using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AiderVS
{
    /// <summary>The Skills panel: the visible side of the skill adapter (see SkillAdapter).</summary>
    public partial class ChatControl
    {
        SkillStore _skills;
        readonly ObservableCollection<SkillRow> _skillRows = new ObservableCollection<SkillRow>();
        bool _loadingSkills;

        void InitSkills()
        {
            SkillList.ItemsSource = _skillRows;
        }

        void ShowSkillSettings()
        {
            AutoSkillsCheck.IsChecked = _settings.SkillsEnabled;
            SmartSkillsCheck.IsChecked = _settings.SmartSkills;
            SkillFoldersBox.Text = _settings.SkillFolders;
        }

        /// <summary>Scans for skills (in the background) and fills the list.</summary>
        async Task ReloadSkillsAsync()
        {
            var folder = CurrentFolder();
            var extra = _settings.SkillFolders;
            var trusted = _settings.TrustedFolders.ToList();

            var store = await Task.Run(() => SkillStore.Load(folder, extra, trusted));
            _skills = store;

            var disabled = new HashSet<string>(_settings.DisabledSkills, StringComparer.OrdinalIgnoreCase);
            var always = new HashSet<string>(_settings.AlwaysSkills, StringComparer.OrdinalIgnoreCase);

            _loadingSkills = true;
            _skillRows.Clear();
            foreach (var skill in store.Skills)
            {
                var row = new SkillRow(skill, OnSkillRowChanged);
                row.Load(!disabled.Contains(skill.Name), always.Contains(skill.Name));
                _skillRows.Add(row);
            }
            _loadingSkills = false;

            SkillInfo.Text = store.Skills.Count == 0
                ? "No skills found. Add a folder below, or put skills in .agents\\skills."
                : store.Skills.Count + " found" + (store.Diagnostics.Count > 0 ? " (" + store.Diagnostics.Count + " notes)" : "");
            SkillInfo.ToolTip = store.Diagnostics.Count > 0 ? string.Join("\n", store.Diagnostics) : null;

            TrustButton.Visibility = store.PendingTrust.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TrustButton.ToolTip = store.PendingTrust.Count > 0
                ? "Skills in this project, not loaded yet:\n" + string.Join("\n", store.PendingTrust) +
                  "\n\nThey are loaded only after you trust the project, because they can steer what Aider does."
                : null;

            RefreshSkillHeader();
        }

        void OnSkillRowChanged(SkillRow row)
        {
            if (_loadingSkills) return;

            SetMembership(_settings.DisabledSkills, row.Name, !row.Enabled);
            SetMembership(_settings.AlwaysSkills, row.Name, row.Always);
            _settings.Save();
            RefreshSkillHeader();
        }

        static void SetMembership(List<string> list, string name, bool member)
        {
            list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (member) list.Add(name);
        }

        /// <summary>The header says how many skills exist, and how many are sent with every message (the only ones that cost anything).</summary>
        void RefreshSkillHeader()
        {
            var always = _skillRows.Count(r => r.Enabled && r.Always);
            SkillsExpander.Header = _skillRows.Count == 0
                ? "Skills: none found"
                : "Skills: " + _skillRows.Count + " found" + (always > 0 ? ", " + always + " sent every time" : "");
        }

        /// <summary>Skills offered by the / and $ autocomplete.</summary>
        List<FileEntry> SkillSuggestions(string query)
        {
            return _skillRows
                .Where(r => r.Enabled && r.Skill.UserInvocable && (query.Length == 0 || r.Name.ToLowerInvariant().Contains(query)))
                .OrderBy(r => r.Name.ToLowerInvariant().StartsWith(query) ? 0 : 1)
                .ThenBy(r => r.Name)
                .Take(12)
                .Select(r => new FileEntry
                {
                    Rel = r.Name,
                    Full = r.Name,
                    IsSkill = true,
                    Detail = (r.Skill.ArgumentHint.Length > 0 ? r.Skill.ArgumentHint + "  " : "") +
                             (r.Skill.Description.Length > 100 ? r.Skill.Description.Substring(0, 100) + "..." : r.Skill.Description)
                })
                .ToList();
        }

        /// <summary>Shows in the chat exactly what the adapter would hand Aider for this skill.</summary>
        void PreviewSkill_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is SkillRow row)) return;

            var text = SkillAdapter.Preview(row.Skill, CurrentFolder(), _settings.SkillBudgetChars);
            AddInfo("**Skill preview: " + row.Name + "**\nThis is what the skill adapter hands Aider, after translating the skill.\n\n" + text);
            ScrollToEnd();
        }

        void TrustSkills_Click(object sender, RoutedEventArgs e)
        {
            var folder = CurrentFolder();
            if (!_settings.TrustedFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                _settings.TrustedFolders.Add(folder);
            _settings.Save();
            var ignored = ReloadSkillsAsync();
        }

        void RefreshSkills_Click(object sender, RoutedEventArgs e)
        {
            var ignored = ReloadSkillsAsync();
        }

        void SkillMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;

            _settings.SkillsEnabled = AutoSkillsCheck.IsChecked == true;
            _settings.SmartSkills = SmartSkillsCheck.IsChecked == true;
            _settings.Save();
        }

        void SkillFolders_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            if (_settings.SkillFolders == SkillFoldersBox.Text.Trim()) return;

            _settings.SkillFolders = SkillFoldersBox.Text.Trim();
            _settings.Save();
            var ignored = ReloadSkillsAsync();
        }
    }
}

