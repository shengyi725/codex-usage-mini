using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexUsageMini
{
    public sealed class WidgetWindow : Window
    {
        private readonly StackPanel body = new StackPanel();
        private readonly TextBlock footer = new TextBlock();
        private readonly Button refresh;
        private readonly Button pin;
        private readonly Preferences preferences;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer();
        private readonly DispatcherTimer clockTimer = new DispatcherTimer();
        private readonly bool testMode;
        private Forms.NotifyIcon tray;
        private Drawing.Icon trayIcon;
        private UsageSnapshot latest;
        private UsageSnapshot preview;
        private bool busy;
        private bool failed;
        private string problem;
        private SubscriptionReadResult subscription;
        private bool subscriptionBusy;
        private Palette skin;
        private Brush Ink { get { return skin.Ink; } }
        private Brush Muted { get { return skin.Muted; } }
        private Brush Track { get { return skin.Track; } }
        private Brush Neutral { get { return skin.Muted; } }
        private Border shell;
        private Grid scaledRoot;
        private ScrollViewer scroll;
        private Border compactShell;
        private Button compactButton, compactMore, fullPercentageButton, compactView, expandView;
        private TextBlock compactNumber;
        private Point compactPress;
        private TextBlock brandTitle, brandCaption;
        private readonly System.Collections.Generic.List<Button> actionButtons = new System.Collections.Generic.List<Button>();
        private HistoryImporter history;
        private HistoryWindow historyWindow;
        private SettingsWindow settingsWindow;
        private bool historyBusy;
        private string historyStatus = "Importing local history";
        private readonly DispatcherTimer historyTimer = new DispatcherTimer();
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        public WidgetWindow(bool testMode) : this(testMode, testMode ? new Preferences() : Preferences.Load()) { }
        private WidgetWindow(bool testing, Preferences prefs)
        {
            testMode = testing;
            preferences = prefs; preferences.Normalize(); skin = Palette.For(preferences.Theme);
            Title = "Codex Usage Mini";
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = preferences.Pinned;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = Ink;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Rect work = SystemParameters.WorkArea;
            Left = Double.IsNaN(preferences.Left) || Double.IsInfinity(preferences.Left) || preferences.Left < work.Left || preferences.Left > work.Right - 354 * preferences.Scale ? work.Right - 354 * preferences.Scale - 22 : preferences.Left;
            Top = Double.IsNaN(preferences.Top) || Double.IsInfinity(preferences.Top) || preferences.Top < work.Top || preferences.Top > work.Bottom - 430 ? work.Top + 52 : preferences.Top;

            shell = new Border { Background = ColorBrush("#111B1C"), BorderBrush = ColorBrush("#344344"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Margin = new Thickness(9), Padding = new Thickness(21, 18, 21, 17), Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .28, BlurRadius = 14, ShadowDepth = 3 } };
            var stack = new StackPanel();
            shell.Child = stack;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 25), Background = Brushes.Transparent };
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock || e.OriginalSource is DockPanel || e.OriginalSource is Border) { try { DragMove(); } catch (InvalidOperationException) { } } };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            pin = Action("\uE718", "Always on top", delegate { TogglePin(); }, true);
            actions.Children.Add(pin);
            actions.Children.Add(Action("\uE921", "Hide to tray", delegate { Hide(); }, true));
            actions.Children.Add(Action("\uE8BB", "Quit", delegate { Close(); }, true));
            header.Children.Add(actions);
            var brand = new StackPanel();
            brandTitle = Text("Codex", 16, Ink, FontWeights.SemiBold); brand.Children.Add(brandTitle);
            var subtitle = Text("U S A G E  M I N I", 9, Muted, FontWeights.Medium); brandCaption = subtitle;
            subtitle.Margin = new Thickness(0, 3, 0, 0);
            brand.Children.Add(subtitle);
            header.Children.Add(brand);
            stack.Children.Add(header);
            stack.Children.Add(body);

            var footerPanel = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
            refresh = Action("\uE72C", "Refresh now", async delegate { preview = null; await RefreshAsync(); }, true);
            DockPanel.SetDock(refresh, Dock.Right);
            footerPanel.Children.Add(refresh);
            footer.FontSize = 10;
            footer.Foreground = Muted;
            footer.VerticalAlignment = VerticalAlignment.Center;
            var utilities = new StackPanel { Orientation = Orientation.Horizontal };
            utilities.Children.Add(Action("\uE9D9", "Hourly usage and model history", delegate { OpenHistory(); }, true));
            utilities.Children.Add(Action("\uE713", "Appearance and plan settings", delegate { OpenSettings(); }, true));
            compactView = Action("Percentage only", "Switch to the smaller percentage-only view", delegate { ToggleCompact(); }, false);
            compactView.Width = 116; compactView.FontSize = 11; compactView.Margin = new Thickness(8, 0, 0, 0);
            utilities.Children.Add(compactView);
            footerPanel.Children.Add(utilities);
            stack.Children.Add(footerPanel); footer.Margin = new Thickness(0, 9, 0, 0); stack.Children.Add(footer);
            scroll = new ScrollViewer { Content = shell, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(200, work.Height - 32) };
            scaledRoot = new Grid { Width = 354 }; scaledRoot.Children.Add(scroll); Content = scaledRoot;
            compactNumber = new TextBlock { FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
            compactButton = PercentageButton(compactNumber, "Expand full widget", delegate { ToggleCompact(); });
            compactButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            compactButton.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { compactPress = e.GetPosition(this); };
            compactButton.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (e.LeftButton != MouseButtonState.Pressed || !compactButton.IsMouseCaptured) return;
                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - compactPress.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - compactPress.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                // Releasing capture cancels the button click before starting the window drag.
                compactButton.ReleaseMouseCapture(); e.Handled = true;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            var compactLayout = new Grid(); compactLayout.Children.Add(compactButton);
            expandView = Action("\u2197", "Expand full widget", delegate { ToggleCompact(); }, false);
            expandView.Width = 22; expandView.Height = 22; expandView.FontSize = 15; expandView.Margin = new Thickness(2, 2, 0, 0); expandView.HorizontalAlignment = HorizontalAlignment.Left; expandView.VerticalAlignment = VerticalAlignment.Top;
            expandView.Opacity = 0; expandView.IsHitTestVisible = false; compactLayout.Children.Add(expandView);
            compactMore = Action("\uE712", "Usage history, appearance, and more", delegate {
                ContextMenu menu = MakeMenu(); menu.PlacementTarget = compactMore; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
            }, true);
            compactMore.Width = 22; compactMore.Height = 22; compactMore.Margin = new Thickness(0, 2, 2, 0); compactMore.HorizontalAlignment = HorizontalAlignment.Right; compactMore.VerticalAlignment = VerticalAlignment.Top;
            compactMore.Opacity = 0; compactMore.IsHitTestVisible = false; compactLayout.Children.Add(compactMore);
            System.Action revealOptions = delegate { bool visible = compactLayout.IsMouseOver || compactLayout.IsKeyboardFocusWithin; compactMore.Opacity = visible ? 1 : 0; compactMore.IsHitTestVisible = visible; expandView.Opacity = visible ? 1 : 0; expandView.IsHitTestVisible = visible; };
            compactLayout.MouseEnter += delegate { revealOptions(); }; compactLayout.MouseLeave += delegate { revealOptions(); }; compactLayout.IsKeyboardFocusWithinChanged += delegate { revealOptions(); };
            compactShell = new Border { Width = 158, Height = 86, Padding = new Thickness(0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Margin = new Thickness(9), Child = compactLayout, Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .28, BlurRadius = 14, ShadowDepth = 3 } };
            // With its shadow margin the compact window is 176 x 104; text does not inherit the full-view scale.
            Content = preferences.Compact ? (object)compactShell : scaledRoot;
            SizeChanged += delegate { ClampToWorkArea(); };
            ContextMenu = MakeMenu();
            refreshTimer.Interval = TimeSpan.FromSeconds(60);
            refreshTimer.Tick += async delegate { if (preview == null) await RefreshAsync(); };
            clockTimer.Interval = TimeSpan.FromSeconds(15);
            clockTimer.Tick += delegate { Render(); };
            Loaded += async delegate {
                if (!testMode) { history = new HistoryImporter(System.IO.Path.Combine(LocalData.DirectoryPath, "usage-history.json")); MakeTray(); refreshTimer.Start(); clockTimer.Start(); historyTimer.Start(); StartHistoryRefresh(); await Task.WhenAll(RefreshSubscriptionAsync(), RefreshAsync()); }
            };
            Closed += delegate {
                lifetime.Cancel(); refreshTimer.Stop(); clockTimer.Stop(); historyTimer.Stop();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayIcon != null) trayIcon.Dispose();
                if (!testMode) { preferences.Pinned = Topmost; preferences.Left = Left; preferences.Top = Top; preferences.Save(); }
            };
            PreviewKeyDown += async delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.F5) { preview = null; await RefreshAsync(); e.Handled = true; }
                if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
                if (e.Key == Key.M && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ToggleCompact(); e.Handled = true; }
            };
            historyTimer.Interval = TimeSpan.FromSeconds(60); historyTimer.Tick += delegate { StartHistoryRefresh(); };
            ApplyAppearance();
        }

        private static SolidColorBrush ColorBrush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
        private static TextBlock Text(string value, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = color, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        }
        private Button Action(string value, string tip, RoutedEventHandler action, bool glyph)
        {
            var button = new Button { Content = value, ToolTip = tip, Width = 27, Height = 27, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Foreground = Muted, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12, FontFamily = new FontFamily(glyph ? "Segoe MDL2 Assets" : "Segoe UI") };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension("WidgetTrack")));
            template.Triggers.Add(hover);
            button.Template = template;
            actionButtons.Add(button); button.Click += action;
            return button;
        }

        private static Button PercentageButton(TextBlock number, string tip, RoutedEventHandler click)
        {
            var button = new Button { Content = number, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), ToolTip = tip, Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            chrome.AppendChild(presenter); template.VisualTree = chrome; button.Template = template;
            System.Windows.Automation.AutomationProperties.SetName(button, tip); button.Click += click; return button;
        }
        public Preferences Settings { get { return preferences; } }
        public bool IsCompact { get { return preferences.Compact; } }
        public void ToggleCompact()
        {
            preferences.Compact = !preferences.Compact;
            Content = preferences.Compact ? (object)compactShell : scaledRoot;
            ContextMenu = MakeMenu(); UpdateLayout(); ClampToWorkArea();
            if (!testMode) { preferences.Left = Left; preferences.Top = Top; preferences.Save(); }
        }
        private void ClampToWorkArea()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, Math.Min(Left, work.Right - ActualWidth));
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - ActualHeight));
        }
        public void ApplyAppearance()
        {
            preferences.Normalize(); skin = Palette.For(preferences.Theme); Resources["WidgetTrack"] = skin.Track;
            Opacity = 1 - preferences.Transparency / 100;
            scaledRoot.LayoutTransform = new ScaleTransform(preferences.Scale, preferences.Scale);
            scroll.MaxHeight = Math.Max(200, (SystemParameters.WorkArea.Height - 32) / preferences.Scale);
            shell.Background = skin.Background; shell.BorderBrush = skin.Border;
            compactShell.Background = skin.Background; compactShell.BorderBrush = skin.Border;
            brandTitle.Foreground = skin.Ink; brandCaption.Foreground = skin.Muted; footer.Foreground = skin.Muted;
            foreach (Button button in actionButtons) button.Foreground = skin.Muted;
            compactView.Foreground = skin.Ink; compactView.Background = skin.Surface;
            ContextMenu = MakeMenu(); UpdatePin(); Render();
        }
        public void OpenSettings()
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow(preferences, ApplyAppearance, latest == null ? preferences.CachedPlan : latest.Plan, latest == null ? preferences.CachedAccountKey : latest.AccountKey, !testMode, SubscriptionInfo, RefreshSubscriptionAsync) { Owner = this };
            settingsWindow.Closed += delegate { settingsWindow = null; }; settingsWindow.Show();
        }
        public void OpenHistory()
        {
            if (historyWindow != null) { historyWindow.Show(); historyWindow.Activate(); return; }
            historyWindow = new HistoryWindow(delegate { return history == null ? new System.Collections.Generic.List<UsageEntry>() : history.Snapshot(); }, delegate { return historyStatus; }, preferences.Theme) { Owner = this };
            historyWindow.Closed += delegate { historyWindow = null; }; historyWindow.Show();
        }
        private async void StartHistoryRefresh()
        {
            if (historyBusy || history == null || lifetime.IsCancellationRequested) return;
            historyBusy = true;
            try {
                ImportResult result;
                do {
                    result = await Task.Run(delegate { return history.Refresh(lifetime.Token); });
                    historyStatus = result.More ? "Importing available history…" : result.Notice ?? "Up to date";
                    if (historyWindow != null) historyWindow.RefreshView();
                    if (result.More) await Task.Delay(500, lifetime.Token);
                } while (result.More && !lifetime.IsCancellationRequested);
            } catch (OperationCanceledException) { }
            catch { historyStatus = "History import unavailable; saved records are still available."; }
            finally { historyBusy = false; }
        }
        public void TogglePin() { Topmost = !Topmost; UpdatePin(); }
        private void UpdatePin() { pin.Foreground = Topmost ? Ink : Muted; pin.ToolTip = Topmost ? "Unpin from top" : "Pin on top"; }
        public void ShowWidget() { Show(); WindowState = WindowState.Normal; Activate(); }
        public void PreviewAllowance(double remaining) { preview = UsageModel.Demo(remaining); Render(); }
        public void PreviewSnapshot(UsageSnapshot snapshot) { preview = snapshot; Render(); }
        internal void PreviewSubscription(SubscriptionSnapshot data) { subscription = new SubscriptionReadResult { Snapshot = data }; Render(); }
        public void PreviewError() { preview = null; latest = UsageModel.Demo(77); failed = true; problem = "Connection unavailable. Check your connection and refresh."; Render(); }

        public SubscriptionDisplay SubscriptionInfo()
        {
            return SubscriptionDisplay.Create(subscription, preferences, latest == null ? preferences.CachedAccountKey : latest.AccountKey, DateTimeOffset.Now);
        }
        public async Task RefreshSubscriptionAsync()
        {
            if (testMode || subscriptionBusy || lifetime.IsCancellationRequested) return;
            if (!preferences.DetectPlanExpiry) { Render(); return; }
            subscriptionBusy = true;
            try {
                string accountKey = latest == null ? preferences.CachedAccountKey : latest.AccountKey;
                subscription = await Task.Run(delegate { return CodexSubscriptionReader.Read(HistoryImporter.CodexHome, accountKey, DateTimeOffset.Now, lifetime.Token); });
            } catch (OperationCanceledException) { }
            finally {
                subscriptionBusy = false;
                if (!lifetime.IsCancellationRequested) { Render(); if (settingsWindow != null) settingsWindow.RefreshSubscription(); }
            }
        }
        public async Task RefreshAsync()
        {
            if (busy || lifetime.IsCancellationRequested) return;
            busy = true; refresh.IsEnabled = false; footer.Text = "Connecting to Codex\u2026";
            try {
                UsageSnapshot data = await Task.Run(async delegate { return await new CodexClient().ReadAsync(lifetime.Token); });
                latest = data; failed = false; problem = null;
                if (!testMode && (preferences.CachedPlan != data.Plan || preferences.CachedAccountKey != data.AccountKey)) { preferences.CachedPlan = data.Plan; preferences.CachedAccountKey = data.AccountKey; preferences.Save(); }
                if (!testMode && !String.IsNullOrEmpty(preferences.PlanExpiry) && String.IsNullOrEmpty(preferences.ExpiryAccountKey) && !String.IsNullOrEmpty(data.AccountKey)) { preferences.ExpiryAccountKey = data.AccountKey; preferences.Save(); }
            }
            catch (OperationCanceledException) { }
            catch (FileNotFoundException ex) { failed = true; problem = ex.Message; }
            catch (FormatException ex) { failed = true; problem = ex.Message; }
            catch (TimeoutException ex) { failed = true; problem = ex.Message; }
            catch { failed = true; problem = "Live allowance is unavailable. Retrying automatically; local history still works."; }
            finally { busy = false; if (!lifetime.IsCancellationRequested) { refresh.IsEnabled = true; Render(); } }
            if (!lifetime.IsCancellationRequested) await RefreshSubscriptionAsync();
        }

        public void Render()
        {
            UsageSnapshot data = preview ?? latest;
            QuotaWindow limit = data == null ? null : data.Limiting;
            bool stale = preview == null && (failed || UsageModel.IsStale(data, DateTimeOffset.Now));
            Brush accent = limit == null || stale ? Neutral : data.Blocked ? Accent(AllowanceLevel.Red) : Accent(UsageModel.Level(limit.Remaining));
            compactNumber.Foreground = accent; compactNumber.Inlines.Clear();
            compactNumber.Inlines.Add(new System.Windows.Documents.Run(limit == null ? "\u2014" : UsageModel.Percent(limit.Remaining).TrimEnd('%')) { FontSize = 48 });
            if (limit != null) compactNumber.Inlines.Add(new System.Windows.Documents.Run("%") { FontSize = 26 });
            string compactStatus = limit == null ? "Allowance unavailable" : UsageModel.Percent(limit.Remaining) + " remaining" + (stale ? " (last known; awaiting fresh data)" : data.Blocked ? " (usage blocked)" : "");
            compactButton.ToolTip = compactStatus + " · click to expand; drag to move";
            System.Windows.Automation.AutomationProperties.SetName(compactButton, compactStatus + ". Expand full widget");
            body.Children.Clear();
            var heading = new DockPanel();
            string badge = preview != null ? "PREVIEW" : data == null ? (failed ? "OFFLINE" : "\u00b7\u00b7\u00b7") : String.IsNullOrEmpty(data.Plan) ? "LIVE" : data.Plan.ToUpperInvariant();
            var plan = new Border { Background = Track, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Child = Text(badge, 9, Muted, FontWeights.SemiBold) };
            DockPanel.SetDock(plan, Dock.Right); heading.Children.Add(plan);
            var eyebrow = Text(limit == null ? "YOUR ALLOWANCE" : limit.Label.ToUpperInvariant() + " ALLOWANCE", 10, Muted, FontWeights.SemiBold);
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(eyebrow); body.Children.Add(heading);
            var number = new TextBlock { Foreground = accent, FontWeight = FontWeights.SemiBold, Margin = new Thickness(-3, 5, 0, 0) };
            number.Inlines.Add(new System.Windows.Documents.Run(limit == null ? "\u2014" : UsageModel.Percent(limit.Remaining).TrimEnd('%')) { FontSize = 66 });
            if (limit != null) number.Inlines.Add(new System.Windows.Documents.Run("%") { FontSize = 31 });
            fullPercentageButton = PercentageButton(number, "Click for percentage only", delegate { ToggleCompact(); });
            System.Windows.Automation.AutomationProperties.SetName(fullPercentageButton, (limit == null ? "Allowance unavailable" : UsageModel.Percent(limit.Remaining) + " remaining") + ". Collapse to percentage only");
            body.Children.Add(fullPercentageButton);
            body.Children.Add(Text(stale && limit != null ? "last known allowance remaining" : "allowance remaining", 12, Muted, FontWeights.Normal));
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
            status.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            string state = limit == null ? (failed ? "Unable to connect" : "Waiting for your allowance") : stale ? "Awaiting fresh data" : data.Blocked ? "Usage currently blocked" : UsageModel.Level(limit.Remaining) == AllowanceLevel.Green ? "Plenty of room" : UsageModel.Level(limit.Remaining) == AllowanceLevel.Orange ? "Keep an eye on usage" : "Running low";
            status.Children.Add(Text(state, 12, accent, FontWeights.Medium)); body.Children.Add(status);
            body.Children.Add(Bar(limit == null ? 0 : limit.Remaining, accent, 7));
            var used = Text(limit == null ? "Live allowance will appear when Codex reconnects." : UsageModel.Percent(100 - limit.Remaining) + " used  \u00b7  " + limit.BucketName, 10, Muted, FontWeights.Normal);
            used.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(used);
            if (limit != null)
            {
                var resetPanel = new Border { Background = skin.Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 12, 13, 12), Margin = new Thickness(0, 18, 0, 0) };
                var row = new DockPanel();
                var time = Text(UsageModel.Countdown(limit.ResetsAt, DateTimeOffset.Now), 20, Ink, FontWeights.SemiBold);
                time.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(time, Dock.Right); row.Children.Add(time);
                var resetLabel = new StackPanel();
                resetLabel.Children.Add(Text("RESETS IN", 9, Muted, FontWeights.SemiBold));
                var date = Text(limit.ResetsAt.HasValue ? limit.ResetsAt.Value.ToLocalTime().ToString("ddd, MMM d \u00b7 HH:mm", CultureInfo.InvariantCulture) : "Reset time not provided", 10, Muted, FontWeights.Normal);
                date.Margin = new Thickness(0, 5, 10, 0); resetLabel.Children.Add(date); row.Children.Add(resetLabel); resetPanel.Child = row; body.Children.Add(resetPanel);
                foreach (QuotaWindow other in data.Windows.Where(w => w != limit))
                {
                    var extra = new StackPanel { Margin = new Thickness(0, 15, 0, 0) };
                    Brush otherAccent = stale ? Neutral : Accent(UsageModel.Level(other.Remaining));
                    extra.Children.Add(Text(other.BucketName + " \u00b7 " + other.Label + "  /  " + UsageModel.Percent(other.Remaining) + " left", 11, Ink, FontWeights.Medium));
                    var bar = Bar(other.Remaining, otherAccent, 4); bar.Margin = new Thickness(0, 6, 0, 6); extra.Children.Add(bar);
                    extra.Children.Add(Text("Resets in " + UsageModel.Countdown(other.ResetsAt, DateTimeOffset.Now), 10, Muted, FontWeights.Normal)); body.Children.Add(extra);
                }
            }
            {
                var planPanel = new Grid { Margin = new Thickness(0, 15, 0, 0) };
                planPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                planPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
                var planStack = new StackPanel(); planStack.Children.Add(Text(data == null ? "PLAN (LAST KNOWN)" : "PLAN", 9, Muted, FontWeights.SemiBold));
                planStack.Children.Add(Text(String.IsNullOrEmpty(data == null ? preferences.CachedPlan : data.Plan) ? "Not provided" : (data == null ? preferences.CachedPlan : data.Plan).ToUpperInvariant(), 11, Ink, FontWeights.Medium)); planPanel.Children.Add(planStack);
                SubscriptionDisplay subscriptionInfo = SubscriptionInfo();
                bool hideSubscription = preview != null && !(testMode && subscription != null);
                var expiryStack = new StackPanel { ToolTip = hideSubscription ? "Synthetic preview" : subscriptionInfo.Detail }; expiryStack.Children.Add(Text(hideSubscription ? "PLAN EXPIRY" : subscriptionInfo.Heading, 9, Muted, FontWeights.SemiBold));
                expiryStack.Children.Add(Text(hideSubscription ? "Preview only" : subscriptionInfo.Value, 10, Muted, FontWeights.Normal)); Grid.SetColumn(expiryStack, 1); planPanel.Children.Add(expiryStack); body.Children.Add(planPanel);
            }
            if (failed && preview == null)
            {
                var error = Text(problem ?? "Refresh to reconnect.", 11, Muted, FontWeights.Normal); error.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(error);
            }
            footer.Text = busy ? "Connecting to Codex\u2026" : preview != null ? "Preview only \u00b7 refresh to return to live" : latest == null ? "Auto-refreshes every 60 seconds" : (stale ? "Last sync " : "Updated ") + latest.FetchedAt.ToLocalTime().ToString("HH:mm") + "  \u00b7  every 60s";
            if (tray != null) UpdateTray(accent, limit, stale, preview != null);
        }

        private Brush Accent(AllowanceLevel level) { return skin.Allowance(level); }
        private Border Bar(double remaining, Brush accent, double height)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, remaining), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - remaining), GridUnitType.Star) });
            grid.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(height / 2) });
            return new Border { Height = height, Background = Track, CornerRadius = new CornerRadius(height / 2), Child = grid };
        }
        private ContextMenu MakeMenu()
        {
            var menu = new ContextMenu();
            AddMenu(menu.Items, preferences.Compact ? "Show full widget" : "Show percentage only", delegate { ToggleCompact(); });
            AddMenu(menu.Items, "Refresh now", async delegate { preview = null; await RefreshAsync(); });
            AddMenu(menu.Items, "Hourly usage and models", delegate { OpenHistory(); });
            AddMenu(menu.Items, "Appearance and plan…", delegate { OpenSettings(); });
            AddMenu(menu.Items, "Always on top", delegate { TogglePin(); });
            var colors = new MenuItem { Header = "Preview colors" };
            AddMenu(colors.Items, "Green \u00b7 77% remaining", delegate { PreviewAllowance(77); });
            AddMenu(colors.Items, "Orange \u00b7 35% remaining", delegate { PreviewAllowance(35); });
            AddMenu(colors.Items, "Red \u00b7 7% remaining", delegate { PreviewAllowance(7); });
            menu.Items.Add(colors);
            AddMenu(menu.Items, "Hide to tray", delegate { Hide(); });
            AddMenu(menu.Items, "Quit", delegate { Close(); });
            return menu;
        }
        private static void AddMenu(ItemCollection items, string label, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = label }; item.Click += action; items.Add(item);
        }
        private void MakeTray()
        {
            tray = new Forms.NotifyIcon { Text = "Codex Usage Mini", Visible = false };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show widget", null, delegate { ShowWidget(); });
            var viewItem = menu.Items.Add("Show percentage only", null, delegate { ToggleCompact(); ShowWidget(); });
            menu.Opening += delegate { viewItem.Text = preferences.Compact ? "Show full widget" : "Show percentage only"; };
            menu.Items.Add("Refresh now", null, async delegate { preview = null; await RefreshAsync(); });
            menu.Items.Add("Hourly usage and models", null, delegate { OpenHistory(); });
            menu.Items.Add("Appearance and plan…", null, delegate { OpenSettings(); });
            menu.Items.Add("Always on top", null, delegate { TogglePin(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Preview green / 77%", null, delegate { PreviewAllowance(77); ShowWidget(); });
            menu.Items.Add("Preview orange / 35%", null, delegate { PreviewAllowance(35); ShowWidget(); });
            menu.Items.Add("Preview red / 7%", null, delegate { PreviewAllowance(7); ShowWidget(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWidget(); };
            Render(); tray.Visible = true;
        }
        private void UpdateTray(Brush accent, QuotaWindow window, bool stale, bool isPreview)
        {
            Color color = ((SolidColorBrush)accent).Color;
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(color.R, color.G, color.B), 4))
            using (var track = new Drawing.Pen(Drawing.Color.FromArgb(55, 67, 68), 4))
            {
                graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawEllipse(track, 3, 3, 25, 25);
                if (window != null && window.Remaining > 0) graphics.DrawArc(pen, 3, 3, 25, 25, -90, (float)(window.Remaining * 3.6));
                using (var font = new Drawing.Font("Segoe UI", 9, Drawing.FontStyle.Bold))
                using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(color.R, color.G, color.B)))
                using (var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center })
                    graphics.DrawString(stale || window == null ? "?" : Math.Floor(window.Remaining).ToString(CultureInfo.InvariantCulture), font, brush, new Drawing.RectangleF(0, 0, 32, 32), format);
                IntPtr handle = bitmap.GetHicon();
                Drawing.Icon next;
                try { using (var borrowed = Drawing.Icon.FromHandle(handle)) next = (Drawing.Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
                Drawing.Icon old = trayIcon; trayIcon = next; tray.Icon = next; if (old != null) old.Dispose();
            }
            string text = "Codex: " + (window == null ? "connecting" : UsageModel.Percent(window.Remaining) + " left") + (isPreview ? " (preview)" : stale ? " (stale)" : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        internal void CheckCompactView(string folder)
        {
            UpdateLayout(); double width = ActualWidth, height = ActualHeight;
            compactView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            if (!IsCompact || ActualWidth >= width * .6 || ActualHeight >= height * .35) throw new Exception("Percentage-only view did not collapse");
            double compactWidth = ActualWidth, compactHeight = ActualHeight;
            foreach (double remaining in new double[] { 77, 35, 7, 100, 0 }) {
                PreviewAllowance(remaining);
                Color expected = ((SolidColorBrush)Accent(UsageModel.Level(remaining))).Color;
                if (((SolidColorBrush)compactNumber.Foreground).Color != expected) throw new Exception("Compact allowance color failed");
                Capture(Path.Combine(folder, "preview-compact-" + remaining.ToString(CultureInfo.InvariantCulture) + ".png"));
            }
            PreviewError(); UpdateLayout();
            if (((SolidColorBrush)compactNumber.Foreground).Color != ((SolidColorBrush)Neutral).Color) throw new Exception("Stale compact allowance must be gray");
            Capture(Path.Combine(folder, "preview-compact-offline.png"));
            Settings.Scale = .5; ApplyAppearance(); UpdateLayout();
            if (Math.Abs(ActualWidth - compactWidth) > 1 || Math.Abs(ActualHeight - compactHeight) > 1) throw new Exception("Full-view scale changed the compact view");
            foreach (string theme in Palette.Names) { Settings.Theme = theme; PreviewAllowance(77); ApplyAppearance(); Capture(Path.Combine(folder, "preview-compact-" + theme.ToLowerInvariant() + ".png")); }
            Settings.Transparency = 50; ApplyAppearance(); if (Math.Abs(Opacity - .5) > .001) throw new Exception("Compact transparency failed");
            Settings.Scale = 1; Settings.Theme = "Dark"; Settings.Transparency = 0; ApplyAppearance();
            expandView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PreviewAllowance(77); UpdateLayout();
            if (IsCompact || Math.Abs(ActualWidth - width) > 1 || Math.Abs(ActualHeight - height) > 1) throw new Exception("Full-size restoration failed");
        }
        public void Capture(string path)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var stream = File.Create(path)) png.Save(stream);
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool smoke = args.Contains("--smoke");
            int demoIndex = Array.IndexOf(args, "--demo");
            bool testing = smoke || demoIndex >= 0;
            bool created;
            string suffix = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, "Local\\CodexUsageMini." + suffix, out created))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexUsageMini.Show." + suffix))
            {
                if (!created && !testing) { show.Set(); return 0; }
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new WidgetWindow(testing);
                RegisteredWaitHandle listener = null;
                if (!testing) listener = ThreadPool.RegisterWaitForSingleObject(show, delegate { if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new System.Action(window.ShowWidget)); }, null, Timeout.Infinite, false);
                if (demoIndex >= 0) { double value = 77; if (args.Length > demoIndex + 1) Double.TryParse(args[demoIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value); window.PreviewAllowance(Math.Max(0, Math.Min(100, value))); }
                if (smoke)
                {
                    int index = Array.IndexOf(args, "--smoke");
                    string folder = args.Length > index + 1 ? args[index + 1] : "docs";
                    window.ContentRendered += delegate {
                        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new System.Action(delegate {
                            try {
                                window.PreviewAllowance(77); window.Capture(Path.Combine(folder, "preview-green.png"));
                                window.PreviewAllowance(35); window.Capture(Path.Combine(folder, "preview-orange.png"));
                                window.PreviewAllowance(7); window.Capture(Path.Combine(folder, "preview-red.png"));
                                bool oldPin = window.Topmost; window.TogglePin(); if (window.Topmost == oldPin) throw new Exception("Pin toggle failed"); window.TogglePin();
                                window.Hide(); if (window.IsVisible) throw new Exception("Hide failed"); window.ShowWidget();
                                window.PreviewError(); window.Capture(Path.Combine(folder, "preview-offline.png"));
                                var multiple = UsageModel.Demo(77); multiple.Windows.Add(new QuotaWindow { BucketId = "codex", BucketName = "Codex", Label = "5-hour", Remaining = 35, ResetsAt = DateTimeOffset.Now.AddHours(2) });
                                window.PreviewSnapshot(multiple); window.Capture(Path.Combine(folder, "preview-multiple.png"));
                                window.PreviewAllowance(77);
                                foreach (string theme in Palette.Names) { window.Settings.Theme = theme; window.ApplyAppearance(); window.Capture(Path.Combine(folder, "preview-theme-" + theme.ToLowerInvariant() + ".png")); }
                                window.Settings.Theme = "Dark"; window.Settings.Scale = 1; window.ApplyAppearance(); window.UpdateLayout(); double fullWidth = window.ActualWidth; double fullHeight = window.ActualHeight;
                                window.Settings.Scale = .5; window.ApplyAppearance(); window.UpdateLayout();
                                if (Math.Abs(window.ActualWidth / fullWidth - .5) > .03 || Math.Abs(window.ActualHeight / fullHeight - .5) > .03) throw new Exception("Proportional half-scale failed");
                                window.Capture(Path.Combine(folder, "preview-quarter-area.png"));
                                window.Settings.Transparency = 50; window.ApplyAppearance(); if (Math.Abs(window.Opacity - .5) > .001) throw new Exception("Transparency failed");
                                window.Capture(Path.Combine(folder, "preview-transparency.png"));
                                window.Settings.Scale = 1; window.Settings.Transparency = 0; window.ApplyAppearance();
                                window.CheckCompactView(folder);
                                var samples = new System.Collections.Generic.List<UsageEntry>();
                                for (int hour = 0; hour < 24; hour++) { if (hour % 4 == 0) continue; samples.Add(new UsageEntry { Id = "demo-" + hour, UtcTicks = DateTimeOffset.UtcNow.AddHours(-hour).UtcDateTime.Ticks, Model = hour % 3 == 0 ? "gpt-6-luna" : "gpt-6.1-sol", Input = (24-hour)*12000, Cached = (24-hour)*8000, Output = (24-hour)*700, Total = (24-hour)*12700, Precise = true }); }
                                var insights = new HistoryWindow(delegate { return samples; }, delegate { return "Synthetic preview"; }, "Ocean"); insights.Show(); insights.RefreshView(); Ui.Capture(insights, Path.Combine(folder, "preview-history-hours.png")); insights.SelectModels(); Ui.Capture(insights, Path.Combine(folder, "preview-history-models.png")); insights.Close();
                                window.PreviewSubscription(new SubscriptionSnapshot { AccountKey = HistoryImporter.Hash("synthetic-preview"), Plan = "pro", PaidThrough = DateTimeOffset.Now.AddDays(14), CheckedAt = DateTimeOffset.Now.AddHours(-1) });
                                if (!window.SubscriptionInfo().Automatic || window.SubscriptionInfo().Heading != "PAID THROUGH") throw new Exception("Automatic subscription display failed");
                                window.Capture(Path.Combine(folder, "preview-subscription.png"));
                                window.OpenSettings(); var settings = app.Windows.OfType<SettingsWindow>().First(); Ui.Capture(settings, Path.Combine(folder, "preview-settings.png")); settings.Close();
                                File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "PASS: allowance states, pin/hide/show, four themes, proportional half scale, transparency, compact percentage colors and stale state, fixed readable compact size, explicit compact/expand buttons and size restoration, hourly/model history, automatic subscription display, settings.\n");
                            } catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "FAIL: " + ex.Message); }
                            window.Close();
                        }));
                    };
                }
                app.Run(window);
                if (listener != null) listener.Unregister(null);
                if (created) mutex.ReleaseMutex();
            }
            return Environment.ExitCode;
        }
    }
}
