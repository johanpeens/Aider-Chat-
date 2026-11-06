using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace AiderVS
{
    /// <summary>The globe icon, the permission question and the loader for reading the internet.</summary>
    public partial class ChatControl
    {
        TaskCompletionSource<bool> _webAsk;
        CancellationTokenSource _webCts;

        /// <summary>What the web step handed back to the message.</summary>
        sealed class WebStep
        {
            public string Message;        // the request, without /search or /web
            public string ContextFile;    // read-only reference file for Aider
            public string Report = "";    // markdown for the top of the reply
        }

        // ---- The icon -------------------------------------------------------------

        void UpdateWebButton()
        {
            var on = _settings.WebAccess;
            WebGlyph.Opacity = on ? 1 : 0.45;
            WebGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                on ? Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ControlLinkTextBrushKey
                   : Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
            WebButton.ToolTip = on
                ? "Internet access is ON. Aider can read web pages and search the web, but only after you allow each request. Click to turn it off."
                : "Internet access is OFF. Click to let Aider read web pages and search the web. It will always ask first, and it only reads.";
        }

        void WebButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.WebAccess = !_settings.WebAccess;
            _settings.Save();
            UpdateWebButton();
            AddInfo(_settings.WebAccess
                ? "Internet access is on. Paste a web address, or start a message with `/search`, and Aider will ask before reading anything. It only reads: nothing from your code is sent."
                : "Internet access is off.");
        }

        /// <summary>The globe pulses while the internet is being read.</summary>
        void PulseGlobe(bool pulsing)
        {
            if (pulsing)
            {
                WebGlyph.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.2, TimeSpan.FromMilliseconds(550))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }
            else
            {
                WebGlyph.BeginAnimation(UIElement.OpacityProperty, null);
                UpdateWebButton();
            }
        }

        // ---- The step inside Send ---------------------------------------------------

        /// <summary>
        /// If the message asks for the internet: check access is on, ask permission, read, and return the result.
        /// Returns null when nothing was read (no web request, access off, or not allowed).
        /// </summary>
        async Task<WebStep> RunWebStepAsync(string text, string stateFolder, CancellationToken ct)
        {
            var plan = WebReader.Detect(text);
            if (plan == null) return null;

            var turnedOn = false;
            if (!_settings.WebAccess)
            {
                // The globe is off: ask once, and if the answer is yes, turn it on and carry on.
                if (!await AskWebPermissionAsync(plan, ct, turnOn: true))
                {
                    WebLog.LogDenied(plan, CurrentFolder());
                    RefreshHistoryIfOpen();
                    AddInfo("Internet access is off, so nothing was read. Click the globe next to Send to turn it on.");
                    return new WebStep { Message = text };
                }

                _settings.WebAccess = true;
                _settings.Save();
                UpdateWebButton();
                turnedOn = true;
            }

            var remembered = !turnedOn && AlreadyAllowed(plan);
            if (!remembered)
            {
                if (!await AskWebPermissionAsync(plan, ct))
                {
                    WebLog.LogDenied(plan, CurrentFolder());
                    RefreshHistoryIfOpen();
                    AddInfo("Not allowed: nothing was read from the internet.");
                    return new WebStep { Message = text };
                }
            }

            ShowWebLoading();
            try
            {
                _webCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var lines = new System.Collections.Generic.List<string>();
                var result = await WebReader.RunAsync(plan, _settings.WebBudgetChars, line =>
                {
                    lines.Add(line);
                    Dispatcher.BeginInvoke(new Action(() => WebDetail.Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)))));
                }, _webCts.Token);

                // keep a record of what was read, grouped by chat, so it can be looked up later
                WebLog.LogResult(plan, result, remembered, CurrentFolder());
                RefreshHistoryIfOpen();

                var step = new WebStep { Message = plan.Message, Report = result.Report };
                if (result.Context.Contains("<web_page") || result.Context.Contains("<web_search"))
                {
                    System.IO.Directory.CreateDirectory(stateFolder);
                    step.ContextFile = System.IO.Path.Combine(stateFolder, "web-pages.md");
                    System.IO.File.WriteAllText(step.ContextFile, result.Context, new System.Text.UTF8Encoding(false));
                }
                return step;
            }
            catch (OperationCanceledException)
            {
                WebLog.LogStopped(plan, CurrentFolder());
                RefreshHistoryIfOpen();
                throw;
            }
            finally
            {
                _webCts?.Dispose();
                _webCts = null;
                HideWebBar();
            }
        }

        // ---- History page --------------------------------------------------------------

        /// <summary>The clock icon switches the transcript area between the chat and the internet history, and back.</summary>
        void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var show = WebHistoryPage.Visibility != Visibility.Visible;
            if (show) RefreshHistory();

            WebHistoryPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Scroller.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

            HistoryGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                show ? Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ControlLinkTextBrushKey
                     : Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey);
        }

        void RefreshHistory() => CodeText.SetSource(WebHistoryView, WebLog.ToMarkdown());

        void RefreshHistoryIfOpen()
        {
            if (WebHistoryPage.Visibility == Visibility.Visible) RefreshHistory();
        }

        void HistoryClear_Click(object sender, RoutedEventArgs e)
        {
            WebLog.Clear();
            RefreshHistory();
        }

        bool AlreadyAllowed(WebPlan plan)
        {
            var allowed = new System.Collections.Generic.HashSet<string>(_settings.AllowedSites, StringComparer.OrdinalIgnoreCase);
            if (plan.IsSearch) return allowed.Contains("web-search");
            return plan.Urls.Count > 0 && WebReader.HostsOf(plan).All(allowed.Contains);
        }

        // ---- Permission -------------------------------------------------------------

        async Task<bool> AskWebPermissionAsync(WebPlan plan, CancellationToken ct, bool turnOn = false)
        {
            _webAsk = new TaskCompletionSource<bool>();
            _webSuggestion = null;

            WebBar.Visibility = Visibility.Visible;
            WebAllowButton.Content = "Allow";
            WebDenyButton.Content = "Don't allow";
            WebTitle.Text = turnOn
                ? "This needs the internet, which is switched off. Turn it on and allow this?"
                : "Aider wants to use the internet. Allow?";
            WebDetail.Text = WebReader.Describe(plan);
            WebProgress.Visibility = Visibility.Collapsed;
            WebAllowButton.Visibility = Visibility.Visible;
            WebDenyButton.Visibility = Visibility.Visible;
            WebStopButton.Visibility = Visibility.Collapsed;

            WebRemember.Visibility = Visibility.Visible;
            WebRemember.IsChecked = false;
            WebRemember.Content = plan.IsSearch
                ? "Don't ask again for web searches"
                : "Don't ask again for " + string.Join(", ", WebReader.HostsOf(plan).Distinct());

            ScrollToEnd();
            PulseGlobe(true);

            using (ct.Register(() => _webAsk.TrySetResult(false)))
            {
                var allowed = await _webAsk.Task;
                PulseGlobe(false);

                if (allowed && WebRemember.IsChecked == true)
                {
                    var keys = plan.IsSearch ? new[] { "web-search" } : WebReader.HostsOf(plan).Distinct().ToArray();
                    foreach (var key in keys)
                        if (!_settings.AllowedSites.Contains(key, StringComparer.OrdinalIgnoreCase)) _settings.AllowedSites.Add(key);
                    _settings.Save();
                }

                if (!allowed) HideWebBar();
                return allowed;
            }
        }

        void WebAllow_Click(object sender, RoutedEventArgs e)
        {
            if (_webSuggestion != null) { AcceptWebSuggestion(); return; }
            _webAsk?.TrySetResult(true);
        }

        void WebDeny_Click(object sender, RoutedEventArgs e)
        {
            if (_webSuggestion != null) { _webSuggestion = null; HideWebBar(); return; }
            _webAsk?.TrySetResult(false);
        }

        // ---- Ask only is on, but the request is to create or change files ------------------

        bool _askingOverride;

        /// <summary>True when a message reads like an instruction to create or change files (and not like a question).</summary>
        static bool WantsFileChanges(string text)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*(how|what|why|which|when|where|who|is|are|does|do|explain)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return false;

            return System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(create|make|write|generate|build|add|implement|edit|update|modify|refactor|save|scaffold|produce)\b[^.\n]{0,60}\b(files?|class(?:es)?|components?|pages?|scripts?|docs?|documents?|readme|projects?|folders?|methods?|functions?|tests?|\.\w{2,4})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>Uses the same bar as the internet question. The answer applies to this one message only.</summary>
        async Task<bool> AskEditOverrideAsync()
        {
            _askingOverride = true;
            _webAsk = new TaskCompletionSource<bool>();
            _webSuggestion = null;

            WebBar.Visibility = Visibility.Visible;
            WebTitle.Text = "Ask only is on, so nothing would be written.";
            WebDetail.Text = "This looks like a request to create or change files. Let Aider write them this time? Ask only stays on for your next message.";
            WebProgress.Visibility = Visibility.Collapsed;
            WebRemember.Visibility = Visibility.Collapsed;
            WebAllowButton.Content = "Write the files";
            WebDenyButton.Content = "Just answer";
            WebAllowButton.Visibility = Visibility.Visible;
            WebDenyButton.Visibility = Visibility.Visible;
            WebStopButton.Visibility = Visibility.Collapsed;
            ScrollToEnd();

            try { return await _webAsk.Task; }
            finally
            {
                HideWebBar();
                _askingOverride = false;
            }
        }

        // ---- When the model says "I can't browse" -------------------------------------

        string _webSuggestion;   // the request to search for, while the suggestion is on screen

        /// <summary>
        /// Aider's model cannot browse by itself. When it says so, offer to read the web for it and ask again.
        /// The user still has to agree, and the usual permission question follows.
        /// </summary>
        void SuggestWebSearch(string request)
        {
            if (_webAsk != null && !_webAsk.Task.IsCompleted) return;   // a permission question is already open

            _webSuggestion = request;
            WebBar.Visibility = Visibility.Visible;
            WebTitle.Text = "The model says it can't browse the internet.";
            WebDetail.Text = "I can search the web for this request and give the model what I find. You will be asked before anything is read.";
            WebProgress.Visibility = Visibility.Collapsed;
            WebRemember.Visibility = Visibility.Collapsed;
            WebAllowButton.Content = "Search the web and ask again";
            WebDenyButton.Content = "No thanks";
            WebAllowButton.Visibility = Visibility.Visible;
            WebDenyButton.Visibility = Visibility.Visible;
            WebStopButton.Visibility = Visibility.Collapsed;
        }

        void AcceptWebSuggestion()
        {
            var request = _webSuggestion;
            _webSuggestion = null;
            HideWebBar();

            // Send the same request again with /search in front: the normal permission question comes next.
            InputBox.Text = "/search " + request;
            Send_Click(null, null);
        }
        void WebStop_Click(object sender, RoutedEventArgs e) => _webCts?.Cancel();

        // ---- Loader -----------------------------------------------------------------

        void ShowWebLoading()
        {
            WebBar.Visibility = Visibility.Visible;
            WebTitle.Text = "Reading from the internet...";
            WebDetail.Text = "";
            WebRemember.Visibility = Visibility.Collapsed;
            WebAllowButton.Visibility = Visibility.Collapsed;
            WebDenyButton.Visibility = Visibility.Collapsed;
            WebStopButton.Visibility = Visibility.Visible;
            WebProgress.Visibility = Visibility.Visible;
            WebProgress.IsIndeterminate = true;
            PulseGlobe(true);
        }

        void HideWebBar()
        {
            PulseGlobe(false);
            WebProgress.Visibility = Visibility.Collapsed;
            WebBar.Visibility = Visibility.Collapsed;
        }
    }
}
