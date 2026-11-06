using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AiderVS
{
    /// <summary>
    /// The setup wizard: checks Ollama, Aider and git, and downloads or updates what is missing.
    /// It opens by itself on first run (or when something essential is missing) and from the Setup button.
    /// </summary>
    public partial class ChatControl
    {
        readonly ObservableCollection<ModelOption> _modelOptions = new ObservableCollection<ModelOption>();
        SetupStatus _setup;
        CancellationTokenSource _setupCts;
        bool _setupBusy;

        void InitSetup()
        {
            SetupModelList.ItemsSource = _modelOptions;
        }

        static string ModelTag(string model)
        {
            var slash = model.IndexOf('/');
            return slash >= 0 && model.StartsWith("ollama", StringComparison.OrdinalIgnoreCase) ? model.Substring(slash + 1) : model;
        }

        static bool SameModel(string installed, string tag)
        {
            if (!tag.Contains(":")) tag += ":latest";
            return string.Equals(installed, tag, StringComparison.OrdinalIgnoreCase);
        }

        // ---- Opening -------------------------------------------------------------

        /// <summary>First run: look quietly; open the wizard only if something the chat needs is missing.</summary>
        async Task FirstRunCheckAsync()
        {
            var status = await SetupService.CheckAsync(_settings, CancellationToken.None);

            // Ollama is often installed but simply not started: start it and look again before bothering the user.
            if (status.OllamaInstalled && !status.OllamaRunning)
            {
                try
                {
                    SetupService.StartOllama(status.OllamaPath);
                    await Task.Delay(4000);
                    status = await SetupService.CheckAsync(_settings, CancellationToken.None);
                }
                catch { }
            }

            _setup = status;
            var tag = ModelTag(_settings.Model);
            var ready = status.OllamaRunning && status.AiderOk && status.Models.Any(m => SameModel(m.Name, tag));

            if (ready)
            {
                if (!_settings.SetupDone)
                {
                    _settings.SetupDone = true;
                    _settings.Save();
                }
                return;
            }

            SetupOverlay.Visibility = Visibility.Visible;
            ApplySetupStatus();
        }

        async void SetupButton_Click(object sender, RoutedEventArgs e)
        {
            SetupOverlay.Visibility = Visibility.Visible;
            await RecheckAsync();
        }

        void SetupDone_Click(object sender, RoutedEventArgs e)
        {
            if (_setupBusy) return;
            _settings.SetupDone = true;
            _settings.Save();
            SetupOverlay.Visibility = Visibility.Collapsed;
        }

        async void SetupRecheck_Click(object sender, RoutedEventArgs e) => await RecheckAsync();

        async Task RecheckAsync()
        {
            SetBusy(true, "Checking...");
            _setup = await SetupService.CheckAsync(_settings, CancellationToken.None);
            ApplySetupStatus();
            SetBusy(false, "");
        }

        // ---- Showing the state ---------------------------------------------------

        void ApplySetupStatus()
        {
            var s = _setup;
            if (s == null) return;

            SetupRamText.Text = "This PC has about " + s.RamGb.ToString("0") + " GB of memory.";

            // 1 Ollama
            if (s.OllamaRunning)
            {
                SetupOllamaStatus.Text = "✔ Running" + (s.OllamaVersion.Length > 0 ? " (version " + s.OllamaVersion + ")" : "") + ".";
                SetupOllamaButton.Visibility = Visibility.Collapsed;
                SetupOllamaSite.Visibility = Visibility.Collapsed;
            }
            else if (s.OllamaInstalled)
            {
                SetupOllamaStatus.Text = "Installed, but not running.";
                SetupOllamaButton.Content = "Start Ollama";
                SetupOllamaButton.Visibility = Visibility.Visible;
                SetupOllamaSite.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetupOllamaStatus.Text = "Not found. Ollama is the program that runs the AI model on your PC.";
                SetupOllamaButton.Content = "Install Ollama";
                SetupOllamaButton.Visibility = Visibility.Visible;
                SetupOllamaSite.Visibility = Visibility.Visible;
            }

            // 2 Aider
            SetupAiderButton.Visibility = Visibility.Visible;
            if (s.AiderOk)
            {
                SetupAiderStatus.Text = "✔ Installed (" + s.AiderVersion + ").";
                SetupAiderButton.Content = "Check for an Aider update";
            }
            else if (!s.UvFound)
            {
                SetupAiderStatus.Text = "Not found. Aider is installed with 'uv', a small installer for Python tools. It is installed first.";
                SetupAiderButton.Content = "Install uv and Aider";
            }
            else
            {
                SetupAiderStatus.Text = "Not found.";
                SetupAiderButton.Content = "Install Aider";
            }

            // 3 Models
            BuildModelOptions();

            // 4 Git
            SetupGitStatus.Text = s.GitFound ? "✔ Found." : "Not found. Only needed for Agent mode, so Aider can map your project.";
            SetupGitButton.Visibility = s.GitFound ? Visibility.Collapsed : Visibility.Visible;
        }

        void BuildModelOptions()
        {
            var s = _setup;
            var recommended = SetupService.Recommend(s.RamGb);
            var current = ModelTag(_settings.Model);
            var selectedTag = SetupService.Choices.Any(c => c.Tag == current) ? current : recommended.Tag;

            foreach (var old in _modelOptions) old.PropertyChanged -= ModelOption_Changed;
            _modelOptions.Clear();

            foreach (var choice in SetupService.Choices)
            {
                var installed = s.Models.Any(m => SameModel(m.Name, choice.Tag));
                var option = new ModelOption
                {
                    Tag = choice.Tag,
                    Installed = installed,
                    Label = choice.Tag + " (" + choice.Size + "): " + choice.Note +
                            (installed ? "  ✔ installed" : "") +
                            (choice.Tag == recommended.Tag ? "  ★ suits this PC" : ""),
                    Selected = choice.Tag == selectedTag
                };
                option.PropertyChanged += ModelOption_Changed;
                _modelOptions.Add(option);
            }

            // anything else already installed (a model the user pulled earlier)
            foreach (var model in s.Models.Where(m => !SetupService.Choices.Any(c => SameModel(m.Name, c.Tag))))
            {
                var option = new ModelOption
                {
                    Tag = model.Name,
                    Installed = true,
                    Label = model.Name + " (" + SetupService.Megabytes(model.Size) + "): already installed  ✔"
                };
                option.PropertyChanged += ModelOption_Changed;
                _modelOptions.Add(option);
            }

            UpdateDownloadButton();
        }

        void ModelOption_Changed(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelOption.Selected)) UpdateDownloadButton();
        }

        ModelOption SelectedOption => _modelOptions.FirstOrDefault(o => o.Selected);

        void UpdateDownloadButton()
        {
            var option = SelectedOption;
            SetupDownloadButton.Content = option != null && option.Installed ? "Update selected model" : "Download selected model";
        }

        void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetupLog.Visibility = Visibility.Visible;
                SetupLog.AppendText(line + "\n");
                SetupLog.ScrollToEnd();
            }));
        }

        /// <summary>While something installs, the other buttons wait.</summary>
        void SetBusy(bool busy, string message)
        {
            _setupBusy = busy;
            foreach (var button in new Button[] { SetupOllamaButton, SetupAiderButton, SetupDownloadButton, SetupRecheck, SetupDone, SetupGitButton })
                button.IsEnabled = !busy;
            SetupCancelButton.IsEnabled = busy && _setupCts != null;
            if (message.Length > 0) SetupModelStatus.Text = message;
        }

        // ---- Installing ----------------------------------------------------------

        void SetupOllamaSite_Click(object sender, RoutedEventArgs e) => ShellLinks.Open("https://ollama.com/download");
        void SetupGit_Click(object sender, RoutedEventArgs e) => ShellLinks.Open("https://git-scm.com/download/win");

        async void SetupOllama_Click(object sender, RoutedEventArgs e)
        {
            if (_setupBusy) return;
            _setupCts = new CancellationTokenSource();
            SetBusy(true, "");

            try
            {
                if (_setup.OllamaInstalled)
                {
                    SetupModelStatus.Text = "Starting Ollama...";
                    SetupService.StartOllama(_setup.OllamaPath);
                    for (var i = 0; i < 15; i++)
                    {
                        await Task.Delay(1000);
                        var status = await SetupService.CheckAsync(_settings, CancellationToken.None);
                        if (status.OllamaRunning) break;
                    }
                }
                else
                {
                    SetupModelStatus.Text = "Installing Ollama with winget. Windows may ask for permission...";
                    var code = await SetupService.InstallOllamaAsync(Log, _setupCts.Token);
                    if (code != 0)
                        SetupModelStatus.Text = "The install did not finish. Use \"Open ollama.com/download\" and run the installer, then press Check again.";
                }
            }
            finally
            {
                _setupCts = null;
                _setup = await SetupService.CheckAsync(_settings, CancellationToken.None);
                ApplySetupStatus();
                SetBusy(false, "");
            }
        }

        async void SetupAider_Click(object sender, RoutedEventArgs e)
        {
            if (_setupBusy) return;
            _setupCts = new CancellationTokenSource();
            SetBusy(true, "");

            try
            {
                var uv = _setup.UvPath;
                if (uv.Length == 0)
                {
                    SetupModelStatus.Text = "Installing uv with winget...";
                    var code = await SetupService.InstallUvAsync(Log, _setupCts.Token);
                    _setup = await SetupService.CheckAsync(_settings, CancellationToken.None);
                    uv = _setup.UvPath;

                    if (code != 0 || uv.Length == 0)
                    {
                        SetupModelStatus.Text = "Could not install uv automatically. Install it from docs.astral.sh/uv, then press Check again.";
                        ShellLinks.Open("https://docs.astral.sh/uv/getting-started/installation/");
                        return;
                    }
                }

                SetupModelStatus.Text = _setup.AiderOk ? "Checking for an Aider update..." : "Installing Aider (a few hundred MB, one minute)...";
                var result = _setup.AiderOk
                    ? await SetupService.UpgradeAiderAsync(uv, Log, _setupCts.Token)
                    : await SetupService.InstallAiderAsync(uv, Log, _setupCts.Token);

                SetupModelStatus.Text = result == 0 ? "Done." : "Aider did not install. See the log below.";
            }
            finally
            {
                _setupCts = null;
                _setup = await SetupService.CheckAsync(_settings, CancellationToken.None);
                ApplySetupStatus();
                SetBusy(false, "");
            }
        }

        async void SetupDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_setupBusy) return;

            var option = SelectedOption;
            if (option == null) return;

            // Ollama must be running to download anything.
            if (!_setup.OllamaRunning)
            {
                if (!_setup.OllamaInstalled)
                {
                    SetupModelStatus.Text = "Install Ollama first (step 1).";
                    return;
                }
                SetupService.StartOllama(_setup.OllamaPath);
                SetupModelStatus.Text = "Starting Ollama...";
                await Task.Delay(4000);
            }

            _setupCts = new CancellationTokenSource();
            SetBusy(true, "Starting download of " + option.Tag + "...");
            SetupProgress.Visibility = Visibility.Visible;
            SetupProgress.IsIndeterminate = true;

            string error;
            try
            {
                error = await SetupService.PullAsync(_settings, option.Tag, (status, fraction) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (fraction >= 0)
                        {
                            SetupProgress.IsIndeterminate = false;
                            SetupProgress.Value = fraction;
                            SetupModelStatus.Text = status + " (" + (fraction * 100).ToString("0") + "%)";
                        }
                        else
                        {
                            SetupProgress.IsIndeterminate = true;
                            SetupModelStatus.Text = status;
                        }
                    }));
                }, _setupCts.Token);
            }
            finally
            {
                _setupCts = null;
                SetupProgress.Visibility = Visibility.Collapsed;
            }

            if (error == null)
            {
                // use the model that was just downloaded
                _settings.Model = "ollama_chat/" + option.Tag;
                _settings.Save();
                _loadingSettings = true;
                ModelBox.Text = _settings.Model;
                _loadingSettings = false;
                UpdateModelButton();
            }

            _setup = await SetupService.CheckAsync(_settings, CancellationToken.None);
            ApplySetupStatus();
            SetBusy(false, error == null ? "✔ " + option.Tag + " is ready and selected." : error);
        }

        void SetupCancel_Click(object sender, RoutedEventArgs e) => _setupCts?.Cancel();

    }
}
