using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace AiderVS
{
    /// <summary>Tiny base class so WPF bindings update when a property changes.</summary>
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One bubble in the chat transcript.</summary>
    public sealed class ChatMessage : Notify
    {
        string _text = "";

        bool _showActions;
        string _status = "";

        public string Role { get; set; }
        public string Font { get; set; } = "Segoe UI";
        public bool IsUser { get; set; }

        /// <summary>Changes Aider proposed that wait for the user to press Apply or Discard.</summary>
        public PendingChange Pending { get; set; }

        public bool ShowActions { get => _showActions; set => Set(ref _showActions, value); }

        public string Status
        {
            get => _status;
            set { Set(ref _status, value ?? ""); Raise(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrEmpty(_status);

        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }
    }

    /// <summary>A file that will be handed to Aider with the next message.</summary>
    public sealed class ContextItem : Notify
    {
        bool _readOnly;
        bool _pinned;

        public ContextItem(string path) { Path = path; }

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>Read-only files give Aider context but are never edited.</summary>
        public bool ReadOnly { get => _readOnly; set => Set(ref _readOnly, value); }

        /// <summary>Pinned files stay when the Solution Explorer selection changes.</summary>
        public bool Pinned { get => _pinned; set => Set(ref _pinned, value); }
    }

    /// <summary>Code the user had highlighted in the editor.</summary>
    public sealed class EditorSnippet
    {
        public string Path { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Text { get; set; }
    }

    /// <summary>What Aider needs to know, saved in %LOCALAPPDATA%\AiderVS\settings.json.</summary>
    public sealed class AiderSettings
    {
        public string AiderPath { get; set; } = "";
        public string Model { get; set; } = "ollama_chat/qwen2.5-coder:7b";
        public string OllamaBase { get; set; } = "http://127.0.0.1:11434";
        public int NumCtx { get; set; } = 16384;
        public bool AutoCommit { get; set; } = false;
        public bool FollowSelection { get; set; } = true;
        public bool AskOnly { get; set; } = false;

        /// <summary>When off (the default), edits are shown first and only written when you press Apply.</summary>
        public bool AutoApply { get; set; } = false;

        /// <summary>Aider may add and edit any file in the solution; every change still needs Apply.</summary>
        public bool AgentMode { get; set; } = false;

        /// <summary>True once the first-run setup wizard found everything in place (or the user closed it).</summary>
        public bool SetupDone { get; set; } = false;

        // ---- Skills ----
        /// <summary>
        /// Pick skills automatically from the request. Off by default: skills then cost nothing unless you name one
        /// (/name or $name) or mark it "always".
        /// </summary>
        public bool SkillsEnabled { get; set; } = false;

        /// <summary>When picking automatically, let the local model confirm (one short extra call). Off by default.</summary>
        public bool SmartSkills { get; set; } = false;

        /// <summary>Settings layout version, so older saved files can be brought up to date.</summary>
        public int SettingsVersion { get; set; } = 1;

        /// <summary>Extra folders that hold skills, separated by semicolons.</summary>
        public string SkillFolders { get; set; } = "";

        /// <summary>Most characters of skill text sent with one message (about 4 characters per token).</summary>
        public int SkillBudgetChars { get; set; } = 4000;

        public List<string> DisabledSkills { get; set; } = new List<string>();
        public List<string> AlwaysSkills { get; set; } = new List<string>();

        /// <summary>Solution folders whose own skills the user has agreed to load.</summary>
        public List<string> TrustedFolders { get; set; } = new List<string>();

        // ---- Internet access (read-only; always asks first unless the user ticked "don't ask again") ----
        public bool WebAccess { get; set; } = false;

        /// <summary>Sites (and "web-search") the user chose not to be asked about again.</summary>
        public List<string> AllowedSites { get; set; } = new List<string>();

        /// <summary>Most characters of web text sent with one message.</summary>
        public int WebBudgetChars { get; set; } = 6000;
        public string ExtraArgs { get; set; } = "";

        public static string StateRoot =>
            System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AiderVS");

        static string FilePath => System.IO.Path.Combine(StateRoot, "settings.json");

        public static AiderSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var saved = JsonConvert.DeserializeObject<AiderSettings>(File.ReadAllText(FilePath)) ?? new AiderSettings();

                    // Version 2: skills became pay-as-you-go. Earlier versions switched automatic picking on,
                    // which cost an extra model call and prompt space on every message.
                    if (saved.SettingsVersion < 2)
                    {
                        saved.SkillsEnabled = false;
                        saved.SmartSkills = false;
                        saved.SkillBudgetChars = 4000;
                        saved.SettingsVersion = 2;
                        saved.Save();
                    }
                    return saved;
                }
            }
            catch { /* a broken settings file just means defaults */ }
            return new AiderSettings { SettingsVersion = 2 };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(StateRoot);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { /* not worth interrupting the user */ }
        }
    }
}
